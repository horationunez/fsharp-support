namespace JetBrains.ReSharper.Plugins.FSharp.Services.SelectEmbracingConstruct

open JetBrains.Application.Settings
open JetBrains.DocumentModel
open JetBrains.ProjectModel
open JetBrains.ReSharper.Feature.Services.Editor
open JetBrains.ReSharper.Feature.Services.SelectEmbracingConstruct
open JetBrains.ReSharper.Plugins.FSharp.Common.Util
open JetBrains.ReSharper.Plugins.FSharp.ProjectModelBase
open JetBrains.ReSharper.Psi
open JetBrains.ReSharper.Plugins.FSharp.Psi.Parsing
open JetBrains.ReSharper.Plugins.FSharp.Psi.Tree
open JetBrains.ReSharper.Plugins.FSharp.Psi.Util
open JetBrains.ReSharper.Psi.Tree
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.SourceCodeServices
open Microsoft.FSharp.Compiler.SourceCodeServices.AstTraversal

type FSharpSelection(file, range, parentRanges: DocumentRange list) =
    inherit SelectedRangeBase<IFSharpFile>(file, range)
    
    override x.Parent =
        match parentRanges with
        | parent :: rest -> FSharpSelection(file, parent, rest) :> ISelectedRange
        | _ -> null


type FSharpDotSelection(file, document, range: TreeTextRange, parentRanges: DocumentRange list) =
    inherit DotSelection<IFSharpFile>(file, range.StartOffset, range.Length = 0, false)

    override x.CreateTreeNodeSelection(tokenNode) =
        FSharpSelection(file, tokenNode.GetDocumentRange(), parentRanges) :> ISelectedRange

    override x.IsWordToken(token) =
        let tokenType = token.GetTokenType()
        tokenType.IsIdentifier || tokenType.IsKeyword

    override x.IsLiteralToken(token) =
        let tokenType = token.GetTokenType()
        tokenType.IsConstantLiteral || tokenType.IsStringLiteral

    override x.IsSpaceToken(token) = token.GetTokenType().IsWhitespace
    override x.IsNewLineToken(token) = token.GetTokenType() = FSharpTokenType.NEW_LINE
    override x.GetParentInternal(tokenNode) = null
    override x.CreateTokenPartSelection(tokenNode, treeTextRange) = null

[<AbstractClass>]
type FSharpSelectEmbracingConstructProviderBase(settingsStore: ISettingsStore) =
    let getRanges = function
        | TraverseStep.Expr(expr) -> [expr.Range]
        | TraverseStep.Module(moduleDecl) -> [moduleDecl.Range]
        | TraverseStep.ModuleOrNamespace(moduleOrNamespaceDecl) -> [moduleOrNamespaceDecl.Range]
        | TraverseStep.TypeDefn(typeDefn) -> [typeDefn.Range]
        | TraverseStep.MemberDefn(memberDefn) -> [memberDefn.Range]
        | TraverseStep.MatchClause(matchClause) -> [matchClause.Range; matchClause.RangeOfGuardAndRhs]
        | TraverseStep.Binding(binding) -> [binding.RangeOfHeadPat; binding.RangeOfBindingAndRhs; binding.RangeOfBindingSansRhs]
        
    abstract IsAvailableImpl: IPsiSourceFile -> bool

    interface ISelectEmbracingConstructProvider with
        member x.IsAvailable(sourceFile) = x.IsAvailableImpl(sourceFile)

        member x.GetSelectedRange(sourceFile, documentRange) =
            let fsFile = sourceFile.GetTheOnlyPsiFile() :?> IFSharpFile
            match isNotNull fsFile, fsFile.ParseResults with
            | true, Some parseResults when parseResults.ParseTree.IsSome ->
                let document = documentRange.Document 
                let pos = document.GetPos(documentRange.StartOffset.Offset)
                let visitor = { new AstVisitorBase<_>() with
                    // todo: cover more cases (inner expressions in bindings, match guards)
                    member x.VisitExpr(path,_,defaultTraverse,expr) =
                        match expr with
                        | SynExpr.Ident _ | SynExpr.Const _ -> Some path
                        | SynExpr.LongIdent _ -> Some (TraverseStep.Expr expr :: path) 
                        | _ -> defaultTraverse expr }

                let containingDeclarations =
                    match fsFile.FindTokenAt(documentRange.EndOffset) with
                    | null -> []
                    | token -> 
                        let rec getParents = function
                        | null -> []
                        | (t: ITreeNode) -> t.GetDocumentRange() :: getParents t.Parent
                        getParents token

                let parentRanges =
                    match Traverse(pos, parseResults.ParseTree.Value, visitor) with
                    | Some traversePath ->
                        List.map getRanges traversePath
                        |> List.concat
                        |> List.map (fun r -> r.ToDocumentRange(document))
                    | None -> []
                    |> List.append containingDeclarations
                    |> List.filter (fun r -> r.Contains(documentRange.EndOffset))
                    |> List.sortBy (fun r -> r.Length)

                FSharpDotSelection(fsFile, document, fsFile.Translate(documentRange), parentRanges) :> ISelectedRange
            | _ -> null

[<ProjectFileType(typeof<FSharpProjectFileType>)>]
type FSharpFileSelectEmbracingConstructProvider(settingsStore: ISettingsStore) =
    inherit FSharpSelectEmbracingConstructProviderBase(settingsStore)

    override x.IsAvailableImpl(sourceFile) = true

[<ProjectFileType(typeof<FSharpScriptProjectFileType>)>]
type FSharpScriptSelectEmbracingConstructProvider(settingsStore: ISettingsStore) =
    inherit FSharpSelectEmbracingConstructProviderBase(settingsStore)

    override x.IsAvailableImpl(sourceFile) = true