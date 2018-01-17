namespace JetBrains.ReSharper.Plugins.FSharp.ProjectModel

open System.Collections.Concurrent
open System.Collections.Generic
open JetBrains.Application.changes
open JetBrains.DataFlow
open JetBrains.ProjectModel
open JetBrains.ReSharper.Plugins.FSharp.Common.Util
open JetBrains.ReSharper.Plugins.FSharp.ProjectModelBase
open JetBrains.ReSharper.Psi.Modules
open JetBrains.Util
open JetBrains.Util.DataStructures

[<SolutionComponent>]
type FSharpScriptPsiModuleFactory(lifetime: Lifetime, solution: ISolution, changeManager: ChangeManager) as this =
    inherit RecursiveProjectModelChangeDeltaVisitor()

    let scriptPaths = HashSet<FileSystemPath>()

    do changeManager.Changed2.Advise(lifetime, this.ProcessChange)

    let add path = lock scriptPaths (fun _ -> scriptPaths.Add(path) |> ignore)
    let remove path = lock scriptPaths (fun _ -> scriptPaths.Remove(path) |> ignore)

    member x.ProcessChange(args: ChangeEventArgs) =
        let change = args.ChangeMap.GetChange<ProjectModelChange>(solution)
        if isNotNull change then
            x.VisitDelta(change)

    override x.VisitDelta(change: ProjectModelChange) =
        match change.ProjectModelElement with
        | :? IProjectFile as file when file.LanguageType.Equals(FSharpScriptProjectFileType.Instance) ->
            if change.IsAdded then
                add file.Location

            if change.IsRemoved then
                remove file.Location

            if change.IsMovedIn || change.IsMovedOut then
                add file.Location
                match change with
                | :? ProjectItemChange as itemChange ->
                    remove itemChange.OldLocation
                | _ -> ()
        | _ -> base.VisitDelta(change)

    interface IPsiModuleFactory with
        member x.Modules = HybridCollection.Empty
