namespace JetBrains.ReSharper.Plugins.FSharp.ProjectModel

open System
open System.Collections.Generic
open JetBrains.Application.changes
open JetBrains.DataFlow
open JetBrains.DocumentManagers
open JetBrains.Metadata.Reader.API
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

type FSharpScriptPsiModule(file: IProjectFile) as this =
    inherit ConcurrentUserDataHolder()

    let project = file.GetProject()
    let solution = file.GetSolution()
    let psiServices = solution.GetPsiServices()

    let decorate f =
        (this :> IDecorableProjectPsiModule).Decorators
        |> Seq.fold (fun state decorator -> state |> f decorator) Seq.empty

    interface IDecorableProjectPsiModule with
        member x.Name = file.Name
        member x.DisplayName = file.Name
        member x.GetPersistentID() = "FSharpScriptModule:" + file.Location.FullPath

        member x.Project = project
        member x.GetSolution() = solution
        member x.GetPsiServices() = psiServices
        member x.TargetFrameworkId = TargetFrameworkId.Default // todo: get highest known

        member x.PsiLanguage = FSharpLanguage.Instance :> _
        member x.ProjectFileType = FSharpScriptProjectFileType.Instance :> _

        member val Decorators = EmptyList.InstanceList with get, set
        member x.SourceFiles = decorate (fun d -> d.OverrideSourceFiles)
        member x.GetReferences(resolveContext) = decorate (fun d -> d.OverrideModuleReferences) // todo: resolve context

        member x.ContainingProjectModule = project :> _
        member x.GetAllDefines() = EmptyList.InstanceList :> _
        member x.IsValid() = project.IsValid() && psiServices.Modules.HasModule(this)


type FSharpScriptModuleHandler(handler, scripts: IProjectFile list) =
    inherit DelegatingProjectPsiModuleHandler(handler)

    let scriptModules = scripts |> List.map (fun s -> FSharpScriptPsiModule(s) :> IPsiModule)

    override x.GetAllModules() =
        let modules = scriptModules |> Seq.append (handler.GetAllModules())
        modules.ToIList()


[<SolutionComponent>]
type FSharpScriptModuleFromProjectHandlerProvider() =
    interface IProjectPsiModuleProviderFilter with
        member x.OverrideHandler(lifetime, project, handler) =
            let scripts =
                project.GetSubItemsRecursively()
                |> Seq.choose (function
                    | :? IProjectFile as projectFile when
                        projectFile.LanguageType.Equals(FSharpScriptProjectFileType.Instance) -> Some projectFile
                    | _ -> None)
                |> List.ofSeq

            if scripts.IsEmpty then handler, null else
            FSharpScriptModuleHandler(handler, scripts) :> _, null

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
