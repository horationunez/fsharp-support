namespace JetBrains.ReSharper.Plugins.FSharp.Daemon.Stages

open System
open System.Text
open JetBrains.Annotations
open JetBrains.DocumentModel
open JetBrains.ReSharper.Feature.Services.Daemon
open JetBrains.ReSharper.Plugins.FSharp.Common.Util
open JetBrains.ReSharper.Plugins.FSharp.Daemon.Highlightings
open JetBrains.ReSharper.Plugins.FSharp.Daemon.Cs.Stages
open JetBrains.ReSharper.Plugins.FSharp.Daemon.Stages.Tooltips
open JetBrains.ReSharper.Plugins.FSharp.Psi.Util
open JetBrains.Util
open Microsoft.FSharp.Compiler.SourceCodeServices

[<AbstractClass; AllowNullLiteral>]
type ErrorsStageProcessBase(daemonProcess, errors: FSharpErrorInfo[]) =
    inherit FSharpDaemonStageProcessBase(daemonProcess)

    // https://github.com/fsharp/FSharp.Compiler.Service/blob/9.0.0/src/fsharp/CompileOps.fs#L246
    // https://github.com/fsharp/FSharp.Compiler.Service/blob/9.0.0/src/fsharp/FSComp.txt
    let [<Literal>] ErrorNumberUndefined = 39
    let [<Literal>] ErronNumberModuleOrNamespaceRequired = 222
    let [<Literal>] ErrorNumberUnused = 1182

    let document = daemonProcess.Document

    let getDocumentRange (error: FSharpErrorInfo) =
        if error.StartLineAlternate = 0 || error.ErrorNumber = ErronNumberModuleOrNamespaceRequired then
            DocumentRange(document, TextRange(0, document.GetLineEndOffsetWithLineBreak(Line.O)))
        else
            let startOffset = document.GetDocumentOffset(error.StartLineAlternate - 1, error.StartColumn)
            let endOffset = document.GetDocumentOffset(error.EndLineAlternate - 1, error.EndColumn)
            DocumentRange(document, TextRange(startOffset, endOffset))

    let createHighlighting(error: FSharpErrorInfo, range: DocumentRange): IHighlighting =
        let message = error.Message
        match error.Severity, error.ErrorNumber with
        | _, ErrorNumberUndefined -> UnresolvedHighlighting(message, range) :> _
        | _, ErrorNumberUnused -> UnusedHighlighting(message, range) :> _
        | FSharpErrorSeverity.Warning, _ -> WarningHighlighting(message, range) :> _
        | _ -> ErrorHighlighting(message, range) :> _

    override x.Execute(committer) =
        let highlightings = ResizeArray<_>(errors.Length)
        let errors = errors |> Array.map (fun error -> error, getDocumentRange error)
        for error, range in errors |> Array.distinctBy (fun (error, range) -> error.Message, range) do
            highlightings.Add(HighlightingInfo(range, createHighlighting(error, range)))
            x.SeldomInterruptChecker.CheckForInterrupt()

        committer.Invoke(DaemonStageResult(highlightings))
