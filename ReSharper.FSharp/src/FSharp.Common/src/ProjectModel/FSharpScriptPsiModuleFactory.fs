namespace JetBrains.ReSharper.Plugins.FSharp.ProjectModel

open System
open System.Collections.Generic
open JetBrains.Application.changes
open JetBrains.DataFlow
open JetBrains.DocumentManagers
open JetBrains.ProjectModel
open JetBrains.ReSharper.Plugins.FSharp.Common.Checker
open JetBrains.ReSharper.Plugins.FSharp.Common.Util
open JetBrains.ReSharper.Plugins.FSharp.ProjectModelBase
open JetBrains.ReSharper.Plugins.FSharp.Psi
open JetBrains.ReSharper.Psi
open JetBrains.ReSharper.Psi.Impl
open JetBrains.ReSharper.Psi.Modules
open JetBrains.Threading
open JetBrains.Util
open JetBrains.Util.DataStructures

type FSharpScriptPsiModule(file: IProjectFile, documentManager: DocumentManager,
                           sourceFilePropertiesManager: PsiSourceFilePropertiesManager) as this =
    inherit ConcurrentUserDataHolder()

    let solution = file.GetSolution()
    let psiServices = solution.GetPsiServices()

    let checkIsValid () = file.IsValid() && psiServices.Modules.HasModule(this)

    interface IPsiModule with
        member x.Name = file.Name
        member x.DisplayName = file.Name
        member x.GetPersistentID() = "FSharpScriptModule:" + file.Location.FullPath

        member x.TargetFrameworkId = null // get highest known
        member x.GetPsiServices() = psiServices
        member x.GetSolution() = solution

        member x.PsiLanguage = FSharpLanguage.Instance :> _
        member x.ProjectFileType = FSharpScriptProjectFileType.Instance :> _

        member x.SourceFiles = Seq.empty
        member x.GetReferences(moduleReferenceResolveContext) = Seq.empty

        member x.ContainingProjectModule = null
        member x.GetAllDefines() = EmptyList.Instance :> _
        member x.IsValid() = checkIsValid ()


type FSharpScriptModuleHandler(handler) =
    inherit DelegatingProjectPsiModuleHandler(handler)

    override x.GetAllModules() = handler.GetAllModules() // todo: add script module here


[<SolutionComponent>]
type FSharpScriptModuleFromProjectHandlerProvider() =
    interface IProjectPsiModuleProviderFilter with
        member x.OverrideHandler(lifetime, project, handler) =
            FSharpScriptModuleHandler(handler) :> _, null

type FSharpScriptModuleFromMiscFilesProvider() =
    class end


//[<SolutionComponent>]
//type FSharpScriptPsiModuleFactory(lifetime, solution: ISolution, changeManager: ChangeManager, documentManager,
//                                  sourceFilePropertiesManager, fsCheckerService: FSharpCheckerService) as this =
//    inherit RecursiveProjectModelChangeDeltaVisitor()
//
//    let locker = JetFastSemiReenterableRWLock()
//    let scriptModules = Dictionary<FileSystemPath, IPsiModule>()
//
//    let processChange (args: ChangeEventArgs) =
//        let change = args.ChangeMap.GetChange<ProjectModelChange>(solution)
//        if isNotNull change then
//            use lock = locker.UsingWriteLock()
//            this.VisitDelta(change)
//
//    let createPsiModule file =
//        FSharpScriptPsiModule(file, documentManager, sourceFilePropertiesManager)
//
//    do
//        use lock = locker.UsingWriteLock()
//        for project in solution.GetAllProjects() do
//            project.GetAllProjectFiles()
//            |> List.ofSeq
//            |> List.filter (fun f -> f.LanguageType.Equals(FSharpScriptProjectFileType.Instance))
//            |> List.iter (fun f -> scriptModules.[f.Location] <- createPsiModule f)
//
//        changeManager.Changed2.Advise(lifetime, processChange)
//
//    override x.VisitDelta(change: ProjectModelChange) =
//        // todo: notify anyone on add/remove?
//        match change.ProjectModelElement with
//        | :? IProjectFile as file when file.LanguageType.Equals(FSharpScriptProjectFileType.Instance) ->
//            if change.IsAdded then
//                scriptModules.[file.Location] <- createPsiModule file
//
//            if change.IsRemoved then
//                scriptModules.Remove(file.Location) |> ignore
//
//            if change.IsMovedIn || change.IsMovedOut then
//                scriptModules.[file.Location] <- createPsiModule file
//                match change with
//                | :? ProjectItemChange as itemChange ->
//                    scriptModules.Remove(itemChange.OldLocation) |> ignore
//                | _ -> ()
//        | _ -> base.VisitDelta(change)
//
//    interface IPsiModuleFactory with
//        member x.Modules =
//            use lock = locker.UsingReadLock()
//            HybridCollection<IPsiModule>(scriptModules.Values)
