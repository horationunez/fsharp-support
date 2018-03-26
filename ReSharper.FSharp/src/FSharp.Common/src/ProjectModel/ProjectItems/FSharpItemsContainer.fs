module rec JetBrains.ReSharper.Plugins.FSharp.ProjectModel.ProjectItems.ItemsContainer

open System
open System.Collections.Generic
open System.IO
open System.Linq
open JetBrains.Application
open JetBrains.Application.Components
open JetBrains.Application.DataContext
open JetBrains.Application.PersistentMap
open JetBrains.Application.Threading
open JetBrains.DataFlow
open JetBrains.Metadata.Reader.API
open JetBrains.Platform.MsBuildHost.Models
open JetBrains.ProjectModel
open JetBrains.ProjectModel.Caches
open JetBrains.ProjectModel.ProjectsHost
open JetBrains.ProjectModel.ProjectsHost.MsBuild
open JetBrains.ProjectModel.ProjectsHost.MsBuild.Structure
open JetBrains.ProjectModel.ProjectsHost.SolutionHost
open JetBrains.ProjectModel.ProjectsHost.SolutionHost.Impl
open JetBrains.ReSharper.Feature.Services.Navigation
open JetBrains.ReSharper.Feature.Services.Navigation.NavigationProviders
open JetBrains.ReSharper.Host.Features.ProjectModel.Editing
open JetBrains.ReSharper.Host.Features.ProjectModel.View
open JetBrains.ReSharper.Host.Features.ProjectModel.View.Appenders
open JetBrains.ReSharper.Host.Features.Util.Tree
open JetBrains.ReSharper.Plugins.FSharp.Common.Util
open JetBrains.ReSharper.Psi
open JetBrains.Threading
open JetBrains.UI.RichText
open JetBrains.Util
open JetBrains.Util.Collections
open JetBrains.Util.DataStructures
open JetBrains.Util.Logging
open JetBrains.Util.PersistentMap

[<SolutionInstanceComponent>]
type FSharpItemsContainer
        (lifetime: Lifetime, logger: ILogger, solutionCaches: ISolutionCaches, solutionHostManager: SolutionHostManager,
         refresher: IFSharpItemsContainerRefresher) =

    let locker = JetFastSemiReenterableRWLock()

    let projectMappings =
        solutionCaches.Db.GetMap("FSharpItemsContainer",
                UnsafeMarshallers.UnicodeStringMarshaller, ProjectMapping.Marshaller)
            .ToOptimized(lifetime) :> IDictionary<_,_>

    let setComparer =
        { new IEqualityComparer<HashSet<_>> with
            member this.Equals(x, y) = x.SetEquals(y)
            member this.GetHashCode(x) = x.Count }
    let targetFrameworkIdIntern = DataIntern(setComparer)

    let tryGetProjectMark (projectItem: IProjectItem) =
        match projectItem.GetProject() with
        | null -> None
        | project ->
            match project.GetProjectMark() with
            | null -> None
            | projectMark -> Some projectMark

    let tryGetProjectMapping (projectMark: IProjectMark): ProjectMapping option =
        tryGetValue projectMark.UniqueProjectName projectMappings

    let tryGetProjectItem (viewItem: FSharpViewItem) =
        tryGetProjectMark viewItem.ProjectItem
        |> Option.bind tryGetProjectMapping
        |> Option.bind (fun mapping -> mapping.TryGetProjectItem(viewItem))

    let getItems (msBuildProject: MsBuildProject) itemTypeFilter yo =
        let items = List<RdProjectItemWithTargetFrameworks>()
        for rdProject in msBuildProject.RdProjects do
            let targetFrameworkId = msBuildProject.GetTargetFramework(rdProject)
            let filter = MsBuildItemTypeFilter(rdProject)
            let projectItems = rdProject.Items |> Seq.filter (fun item -> (itemTypeFilter item.ItemType && yo) || not (filter.FilterByItemType(item.ItemType, item.IsImported()))) |> List.ofSeq
            projectItems
            |> List.filter (fun item -> itemTypeFilter item.ItemType)
            |> List.fold (fun index item ->
                if index < items.Count && items.[index].Item.EvaluatedInclude = item.EvaluatedInclude then
                    items.[index].TargetFrameworkIds.Add(targetFrameworkId) |> ignore
                    index + 1
                else
                    let mutable tmpIndex = index + 1
                    while tmpIndex < items.Count && items.[tmpIndex].Item.EvaluatedInclude <> item.EvaluatedInclude do
                        tmpIndex <- tmpIndex + 1

                    if tmpIndex >= items.Count then
                        items.Insert(index, { Item = item; TargetFrameworkIds = HashSet([targetFrameworkId]) })
                        index + 1
                    else
                        items.[tmpIndex].TargetFrameworkIds.Add(targetFrameworkId) |> ignore
                        tmpIndex + 1) 0 |> ignore
            projectItems |> ignore
        items

    member x.IsValid(viewItem: FSharpViewItem) =
        use lock = locker.UsingReadLock()
        tryGetProjectItem viewItem |> Option.isSome

    interface IFSharpItemsContainer with
        member x.OnProjectLoaded(projectMark, msBuildProject) =
            match msBuildProject with
            | null ->
                use lock = locker.UsingWriteLock()
                projectMappings.Remove(projectMark.UniqueProjectName) |> ignore
            | _ ->

            match projectMark with
            | FSharProjectMark ->
                let compileBeforeItems = getItems msBuildProject isCompileBefore true
                let compileAfterItems = getItems msBuildProject isCompileAfter true 
                let restItems = getItems msBuildProject (changesOrder >> not) false
                let items =
                    compileBeforeItems.Concat(restItems).Concat(compileAfterItems)
                    |> Seq.map (fun item ->
                        { item with TargetFrameworkIds = targetFrameworkIdIntern.Intern(item.TargetFrameworkIds) })
                    |> List.ofSeq
                let targetFrameworkIds = HashSet(msBuildProject.TargetFrameworkIds)

                use lock = locker.UsingWriteLock()
                projectMappings.[projectMark.UniqueProjectName] <-
                    let projectDirectory = projectMark.Location.Directory
                    let projectUniqueName = projectMark.UniqueProjectName
                    let mapping = ProjectMapping(projectDirectory, projectUniqueName, targetFrameworkIds, refresher,
                                                 solutionHostManager, logger)
                    mapping.Update(items)
                    mapping
                refresher.Refresh(projectMark, true)
            | _ -> ()

        member x.OnAddFile(projectMark, itemType, path, linkedPath, relativeTo, relativeToType) =
            use lock = locker.UsingWriteLock()
            tryGetValue projectMark.UniqueProjectName projectMappings
            |> Option.iter (fun mapping ->
                let logicalPath = if isNotNull linkedPath then linkedPath else path
                mapping.AddFile(itemType, path, logicalPath, relativeTo, Option.ofNullable relativeToType))

        member x.OnRemoveFile(projectMark, itemType, location) =
            use lock = locker.UsingWriteLock()
            tryGetValue projectMark.UniqueProjectName projectMappings
            |> Option.iter (fun mapping -> mapping.RemoveFile(itemType, location))

        member x.OnUpdateFile(projectMark, oldItemType, oldLocation, newItemType, newLocation) =
            if not (equalsIgnoreCase oldItemType newItemType) &&
                    (changesOrder oldItemType || changesOrder newItemType) then
                refresher.ReloadProject(projectMark) else

            use lock = locker.UsingWriteLock()
            tryGetValue projectMark.UniqueProjectName projectMappings
            |> Option.iter (fun mapping ->
                mapping.UpdateFile(oldItemType, oldLocation, newItemType, newLocation)
                refresher.Update(projectMark, newLocation))

        member x.OnUpdateFolder(projectMark, oldLocation, newLocation) =
            if oldLocation <> newLocation then
                use lock = locker.UsingWriteLock()
                tryGetValue projectMark.UniqueProjectName projectMappings
                |> Option.iter (fun mapping -> mapping.UpdateFolder(oldLocation, newLocation))

        member x.OnAddFolder(projectMark, path, relativeTo, relativeToType) = ()

        member x.OnRemoveFolder(projectMark, path) =
            null |> ignore
            ()

        member x.CreateFoldersWithParents(folder: IProjectFolder) =
            use lock = locker.UsingReadLock()
            tryGetProjectMark folder
            |> Option.bind tryGetProjectMapping
            |> Option.map (fun mapping ->
                mapping.TryGetProjectItems(folder.Location)
                |> Seq.map (function
                    | FolderItem (_, id) as folderItem ->
                        let parent =
                            match folderItem.Parent with
                            | ProjectItem (FolderItem (_, id)) -> Some (FSharpViewFolder (folder.ParentFolder, id))
                            | _ -> None
                        FSharpViewFolder (folder, id), parent
                    | item -> sprintf "got item %O" item |> failwith)
                |> List.ofSeq)
            |> Option.defaultValue []

        member x.TryGetRelativeChildPath(projectMark, modifiedItem, relativeItem, relativeToType) =
            use lock = locker.UsingReadLock()
            tryGetValue projectMark.UniqueProjectName projectMappings
            |> Option.bind (fun mapping ->
                mapping.TryGetRelativeChildPath(modifiedItem, relativeItem, relativeToType))

        member x.TryGetParentFolderIdentity(viewFile: FSharpViewItem): FSharpViewFolderIdentity option =
            use lock = locker.UsingReadLock()
            tryGetProjectMark viewFile.ProjectItem
            |> Option.bind tryGetProjectMapping 
            |> Option.bind (fun mapping ->
                mapping.TryGetProjectItem(viewFile)
                |> Option.bind (fun item ->
                    match item.Parent with
                    | ProjectItem (FolderItem (_, id)) -> Some id
                    | _ -> None))

        member x.Dump(writer: TextWriter) =
            let container = solutionHostManager.ProjectsHostContainer.GetComponent<ISolutionStructureContainer>()
            let projectMarks = container.ProjectMarks |> List.ofSeq
            use lock = locker.UsingReadLock()
            for KeyValuePair (projectUniqueName, mapping) in projectMappings do
                let projectName =
                    projectMarks
                    |> List.tryFind (fun projectMark -> projectMark.UniqueProjectName = projectUniqueName)
                    |> Option.map (fun projectMark -> projectMark.Name)
                    |> Option.defaultValue projectUniqueName
                writer.WriteLine(projectName)
                mapping.Dump(writer)

        member x.TryGetSortKey(viewItem: FSharpViewItem) =
            use lock = locker.UsingReadLock()
            tryGetProjectItem viewItem |> Option.map (fun item -> item.SortKey)

        member x.IsApplicable(projectItem) =
            use lock = locker.UsingReadLock()
            tryGetProjectMark projectItem
            |> Option.bind tryGetProjectMapping
            |> Option.isSome

        member x.GetProjectItemsPaths(projectMark, targetFrameworkId) =
            tryGetValue projectMark.UniqueProjectName projectMappings
            |> Option.map (fun mapping -> mapping.GetProjectItemsPaths(targetFrameworkId))
            |> Option.defaultValue [| |]

type IFSharpItemsContainer =
    inherit IMsBuildProjectListener
    inherit IMsBuildProjectModificationListener

    abstract member IsApplicable: IProjectItem -> bool
    abstract member TryGetSortKey: FSharpViewItem -> int option
    abstract member TryGetParentFolderIdentity: FSharpViewItem -> FSharpViewFolderIdentity option
    abstract member CreateFoldersWithParents: IProjectFolder -> (FSharpViewFolder * FSharpViewFolder option) list
    abstract member GetProjectItemsPaths: IProjectMark * TargetFrameworkId -> (FileSystemPath * BuildAction)[]
    abstract member Dump: TextWriter -> unit

    abstract member TryGetRelativeChildPath:
            IProjectMark * modifiedItem: FSharpViewItem * relativeItem: FSharpViewItem * RelativeToType ->
            (FileSystemPath * RelativeToType) option


type ProjectMapping(projectDirectory, projectUniqueName, targetFrameworkIds, logger: ILogger) =

    // Files and folders by physical path.
    // For now we assume that a file is only included to a single item type group.
    let files = Dictionary<FileSystemPath, FSharpProjectItem>()
    let folders = OneToListMap<FileSystemPath, FSharpProjectItem>()

    let tryGetProjectMarkForProject projectUniqueName =
        let container = solutionHostManager.ProjectsHostContainer.GetComponent<ISolutionStructureContainer>()
        let projectMark =
            container.ProjectMarks |> Seq.tryFind (fun projectMark -> projectMark.UniqueProjectName = projectUniqueName)
        match projectMark with
        | Some projectMark -> Some projectMark
        | None ->
            logger.Warn(sprintf "Did not find project mark for %s" projectUniqueName)
            None  

    let tryGetFile path =
        tryGetValue path files

    let getFolders path =
        folders.GetValuesSafe(path) |> List.ofSeq

    let getItemsForPath path =
        tryGetFile path
        |> Option.toList
        |> List.append (getFolders path)

    let getNewFolderIdentity path =
        { Identity = (getFolders path |> List.length) + 1 }

    let getChildren (parent: FSharpProjectModelElement) =
        folders.Values
        |> Seq.append files.Values
        |> Seq.filter (fun item -> item.Parent = parent)
        |> Seq.sortBy (fun x -> x.SortKey)

    let getNewSortKey parent =
        getChildren parent |> Seq.length |> (+) 1

    let moveFollowingItems parent sortKeyFrom direction updateItem =
        getChildren parent
        |> Seq.iter (fun item ->
            if item.SortKey >= sortKeyFrom then
                item.ItemInfo.SortKey <-
                    match direction with
                    | MoveDirection.Up -> item.SortKey - 1
                    | MoveDirection.Down -> item.SortKey + 1
                updateItem item)

    let addFolder parent sortKey path updateItem =
        let folderItem = FolderItem(ItemInfo.Create(path, path, parent, sortKey), getNewFolderIdentity path)
        moveFollowingItems parent sortKey MoveDirection.Down updateItem
        folders.Add(path, folderItem)
        ProjectItem folderItem

    let getOrCreateFolder folderRefresher parent path =
        folders.GetValuesSafe(path)
        |> Seq.sortBy (fun item -> item.SortKey)
        |> Seq.tryLast
        |> Option.defaultWith (fun _ ->
            folderRefresher parent

            let itemInfo = ItemInfo.Create(path, path, parent, getNewSortKey parent)
            let folderItem = FolderItem(itemInfo, getNewFolderIdentity path)
            folders.Add(path, folderItem) 
            folderItem)
        |> ProjectItem

    let (|EmptyFolder|_|) projectItem =
        match projectItem with
        | FolderItem _ when getChildren (ProjectItem projectItem) |> Seq.isEmpty -> Some projectItem
        | _ -> None

    let getNewRelativeSortKey (item: FSharpProjectItem) relativeToType =
        match relativeToType with
        | RelativeToType.Before -> item.SortKey
        | RelativeToType.After -> item.SortKey + 1
        | _ -> relativeToType |> sprintf "Got relativeToType %O" |> failwith

    let canBeRelative (projectItem: FSharpProjectItem) (modifiedItem: FSharpProjectItem option) =
        match projectItem, modifiedItem with
        | FileItem _, None -> true
        | FileItem (_, buildAction, _), Some (FileItem (_, modifiedItemBuildAction, _)) ->
            not (buildAction.ChangesOrder()) && not (modifiedItemBuildAction.ChangesOrder()) ||
            buildAction.Equals(modifiedItemBuildAction)

        | EmptyFolder _, None -> true
        | EmptyFolder _, Some (FileItem (_, buildAction, _)) -> not (buildAction.ChangesOrder())

        | _ -> false

    let changeDirection = function
        | RelativeToType.Before -> RelativeToType.After
        | RelativeToType.After -> RelativeToType.Before
        | relativeToType -> relativeToType |> sprintf "Got relativeToType %O" |> failwith

    let createRefreshers () =
        let mutable folderToRefresh = None
        let itemsToUpdate = HashSet<FSharpProjectItem>()

        let refreshFolder folder =
            match folderToRefresh, folder with
            | None, _ | _, Project -> folderToRefresh <- Some folder
            | Some (ProjectItem existingFolder), (ProjectItem newFolder) when
                    newFolder.LogicalPath.IsPrefixOf(existingFolder.LogicalPath) -> folderToRefresh <- Some folder
            | _ -> ()

        let updateItem item =
            itemsToUpdate.Add(item) |> ignore

        let refresh () =
            tryGetProjectMarkForProject projectUniqueName
            |> Option.iter (fun projectMark ->
                match folderToRefresh with
                    | Some Project -> refresher.Refresh(projectMark, false)
                    | Some (ProjectItem (FolderItem (_, id) as folder)) ->
                        refresher.Refresh(projectMark, folder.LogicalPath, id)
                    | _ -> ()
                for item in itemsToUpdate do
                    match item with
                    | FileItem _ -> refresher.Update(projectMark, item.ItemInfo.LogicalPath)
                    | FolderItem (_, id) -> refresher.Update(projectMark, item.ItemInfo.LogicalPath, id))

        refreshFolder, updateItem, refresh

    let tryGetAdjacentItemInParent (relativeItem: FSharpProjectItem) relativeToType =
        let otherRelativeSortKey =
            match relativeToType with
            | RelativeToType.After -> relativeItem.SortKey + 1
            | RelativeToType.Before -> relativeItem.SortKey - 1
            | _ -> relativeToType |> sprintf "Got relativeToType %O" |> failwith
        getChildren relativeItem.Parent
        |> Seq.filter (fun item -> item.SortKey = otherRelativeSortKey)
        |> List.ofSeq
        |> function | item :: [] -> Some item | _ -> None

    let splitFolder (folder: FSharpProjectItem) folderPath splitSortKey itemsUpdater =
        let newFolderPart = addFolder folder.Parent (folder.SortKey + 1) folderPath itemsUpdater

        getChildren (ProjectItem folder)
        |> Seq.filter (fun item -> item.SortKey >= splitSortKey)
        |> List.ofSeq
        |> List.iteri (fun i item ->
            item.ItemInfo.Parent <- newFolderPart
            item.ItemInfo.SortKey <- i + 1)

    let rec traverseParentFolders (item: FSharpProjectModelElement) = seq {
        match item with
        | Project -> ()
        | ProjectItem item ->
            yield item
            yield! traverseParentFolders item.Parent }

    let getTopLevelModifiedParent itemPath (relativeItem: FSharpProjectItem) relativeToType itemsUpdater =
        match relativeItem.Parent with
        | Project -> Project, relativeItem, false
        | ProjectItem relativeItemParent ->
            let commonParentPath = FileSystemPath.GetDeepestCommonParent(relativeItemParent.LogicalPath, itemPath)
            let initialState = relativeItem.Parent, relativeItem, false

            traverseParentFolders (ProjectItem relativeItemParent)
            |> Seq.takeWhile (fun item -> item.LogicalPath <> commonParentPath)
            |> Seq.fold (fun state _ ->
                match state with
                | ProjectItem parent, relativeItem, shouldRefresh ->
                    match tryGetAdjacentItemInParent relativeItem relativeToType with
                    | Some secondRelativeItem ->
                        let sortKey = Math.Max(relativeItem.SortKey, secondRelativeItem.SortKey)
                        let relativeItemParent, secondRelativeItemParentPath =
                            match relativeItem.Parent, secondRelativeItem.Parent with
                            | ProjectItem relativeParent, ProjectItem secondRelativeParent ->
                                relativeParent, secondRelativeParent.LogicalPath
                            | _ -> failwith "item parent"
                        splitFolder relativeItemParent secondRelativeItemParentPath sortKey itemsUpdater
    
                        let relativeParent, relativeItem =
                            match relativeItem.Parent with
                            | ProjectItem item -> item.Parent, item
                            | _ -> failwith "getting parent item of project"
                        relativeParent, relativeItem, true
                    | _ -> parent.Parent, parent, shouldRefresh
                | _ -> sprintf "got project as previous parent: %A" state |> failwith) initialState

    let createFoldersForItem itemPath relativeItem relativeToType folderRefresher itemUpdater =
        let parent, relativeItem, shouldRefresh =
            getTopLevelModifiedParent itemPath relativeItem relativeToType itemUpdater

        let newFolders =
            itemPath.GetParentDirectories()
            |> Seq.takeWhile (fun p -> p <> relativeItem.LogicalPath.Parent)
            |> Seq.rev
            |> List.ofSeq

        if shouldRefresh || not (List.isEmpty newFolders) then
            folderRefresher parent

        let sortKey = getNewRelativeSortKey relativeItem relativeToType
        newFolders |> List.fold (fun (parent, sortKey) folderPath ->
            addFolder parent sortKey folderPath itemUpdater, 1) (parent, sortKey)

    let rec tryGetRelativeChildItem (nodeItem: FSharpProjectItem option) modifiedItem relativeToType =
        nodeItem |> Option.bind (fun item ->
            let children = getChildren (ProjectItem item)
            let relativeChildItem =
                match relativeToType with
                | RelativeToType.Before -> Seq.tryHead children
                | RelativeToType.After -> Seq.tryLast children
                | _ -> relativeToType |> sprintf "Got relativeToType %O" |> failwith
        
            match relativeChildItem with
            | Some item when canBeRelative item modifiedItem -> Some (item, relativeToType)
            | _ -> tryGetRelativeChildItem relativeChildItem modifiedItem relativeToType)

    let getRelativeChildPathImpl (relativeViewItem: FSharpViewItem) modifiedNodeItem relativeToType =
        getItemsForPath relativeViewItem.Location
        |> Seq.tryHead
        |> Option.bind (function
            | FileItem _ as fileItem -> Some (fileItem, relativeToType)
            | FolderItem _ as folderItem ->
                if canBeRelative folderItem modifiedNodeItem then Some (folderItem, relativeToType) else
                tryGetRelativeChildItem (Some (folderItem)) modifiedNodeItem relativeToType)

    let rec renameFolder oldLocation newLocation itemUpdater =
        getFolders oldLocation
        |> List.iter (fun folderItem ->
            folderItem.ItemInfo.LogicalPath <- newLocation
            folderItem.ItemInfo.PhysicalPath <- newLocation
            folders.AddValue(newLocation, folderItem)

            getChildren (ProjectItem folderItem)
            |> List.ofSeq
            |> List.iter (fun childItem ->
                let oldChildLocation = oldLocation / childItem.LogicalPath.Name // todo
                let newChildLocation = newLocation / childItem.LogicalPath.Name

                match childItem with
                | FileItem _ as childFileItem ->
                    childFileItem.ItemInfo.LogicalPath <- newChildLocation
                    childFileItem.ItemInfo.PhysicalPath <- newLocation / childItem.PhysicalPath.Name
                    files.Remove(oldChildLocation) |> ignore
                    files.Add(newChildLocation, childFileItem)
                | FolderItem _ ->
                    renameFolder oldChildLocation newChildLocation ignore)
            itemUpdater folderItem)
        folders.RemoveKey(oldLocation) |> ignore

    let rec removeSplittedFolderIfEmpty folder folderPath folderRefresher itemUpdater =
        let isFolderSplitted path = getFolders path |> List.length > 1

        match folder with
        | ProjectItem (EmptyFolder (FolderItem (_, folderId)) as folderItem) when isFolderSplitted folderPath ->
            getFolders folderPath
            |> List.iter (fun folderItem ->
                match folderItem with
                | FolderItem (_, id) ->
                    if id.Identity > folderId.Identity then id.Identity <- id.Identity - 1
                | _ -> ())

            removeItem folderItem folderRefresher itemUpdater
            folderRefresher folderItem.Parent
        | _ -> ()

    // todo: on remove folder in container? 
    and removeItem (item: FSharpProjectItem) folderRefresher itemUpdater =
        let siblings = getChildren item.Parent |> List.ofSeq
        let itemBefore = siblings |> List.tryFind (fun i -> i.SortKey = item.SortKey - 1)
        let itemAfter = siblings |> List.tryFind (fun i -> i.SortKey = item.SortKey + 1)

        let itemPath =
            match item with
            | FileItem _ -> item.PhysicalPath
            | FolderItem _ -> item.LogicalPath
        match item with
        | FileItem _ | EmptyFolder _ ->
            joinRelativeFoldersIfSplitted itemBefore itemAfter folderRefresher itemUpdater
            match item with
            | FileItem _ -> files.Remove(itemPath) |> ignore
            | _ -> folders.RemoveValue(itemPath, item) |> ignore

            moveFollowingItems item.Parent item.SortKey MoveDirection.Up itemUpdater
            removeSplittedFolderIfEmpty item.Parent itemPath.Parent folderRefresher itemUpdater
        | _ ->
            failwith "removing non-empty folder"

    and joinRelativeFoldersIfSplitted itemBefore itemAfter folderRefresher itemUpdater =
        match itemBefore, itemAfter with
        | Some (FolderItem _ as itemBefore), Some (FolderItem _ as itemAfter) when
                itemBefore.PhysicalPath = itemAfter.PhysicalPath ->

            let folderAfterChildren = getChildren (ProjectItem itemAfter) |> List.ofSeq
            let folderBeforeChildren = getChildren (ProjectItem itemBefore) |> List.ofSeq

            let folderBeforeChildrenCount = folderBeforeChildren |> List.length
            folderAfterChildren |> List.iteri (fun i child ->
                child.ItemInfo.Parent <- ProjectItem itemBefore
                child.ItemInfo.SortKey <- folderBeforeChildrenCount + i + 1)

            folders.RemoveValue(itemAfter.PhysicalPath, itemAfter) |> ignore
            moveFollowingItems itemAfter.Parent itemAfter.SortKey MoveDirection.Up itemUpdater

            let lastChildBefore = List.tryLast folderBeforeChildren
            let firstChildAfter = List.tryHead folderAfterChildren

            joinRelativeFoldersIfSplitted lastChildBefore firstChildAfter folderRefresher itemUpdater
            folderRefresher itemBefore.Parent
        | _ -> ()

    let rec tryGetAdjacentRelativeItem nodeItem modifiedNodeItem relativeToType =
        match nodeItem with
        | Project -> None
        | ProjectItem nodeItem ->
            tryGetAdjacentItemInParent nodeItem relativeToType
            |> Option.bind (fun adjacentItem ->
                if canBeRelative adjacentItem modifiedNodeItem then Some (adjacentItem, relativeToType)
                else
                    tryGetRelativeChildItem (Some adjacentItem) modifiedNodeItem (changeDirection relativeToType)
                    |> Option.map (fun (item, _) -> item, relativeToType))
            |> Option.orElseWith (fun _ ->
                // todo: check item type
                tryGetAdjacentRelativeItem nodeItem.Parent modifiedNodeItem relativeToType)

    let iter f =
        let rec iter (parent: FSharpProjectModelElement) =
            for item in getChildren parent do
                f item
                iter (ProjectItem item)
        iter Project 

    member x.Update(items) =
        let folders = Stack()
        folders.Push(State.Create(projectDirectory, Project))

        let parsePaths (item: RdProjectItem) =
            let path = FileSystemPath.TryParse(item.EvaluatedInclude)
            if path.IsEmpty then None else

            let physicalPath = path.MakeAbsoluteBasedOn(projectDirectory)
            let logicalPath =
                let linkPath = item.GetLink()
                if not (linkPath.IsNullOrEmpty()) then
                    linkPath.MakeAbsoluteBasedOn(projectDirectory)
                elif projectDirectory.IsPrefixOf(physicalPath) then physicalPath
                else projectDirectory.Combine(physicalPath.Name)
            Some (physicalPath, logicalPath)

        for item in items do
            match parsePaths item.Item with
            | Some (physicalPath, logicalPath) ->
                Assertion.Assert(projectDirectory.IsPrefixOf(logicalPath), "Invalid logical path")
                if logicalPath.Directory <> folders.Peek().Path then
                    let commonParent = FileSystemPath.GetDeepestCommonParent(logicalPath.Parent, folders.Peek().Path)
                    while (folders.Peek().Path <> commonParent) do
                        folders.Pop() |> ignore

                    let newFolders =
                        logicalPath.GetParentDirectories() |> Seq.takeWhile (fun p -> p <> commonParent) |> Seq.rev

                    for folderPath in newFolders do
                        let currentState = folders.Peek()
                        currentState.NextSortKey <- currentState.NextSortKey + 1

                        let folder = addFolder currentState.Folder currentState.NextSortKey folderPath ignore
                        folders.Push(State.Create(folderPath, folder))

                let currentState = folders.Peek()
                let parent = currentState.Folder
                currentState.NextSortKey <- currentState.NextSortKey + 1

                match item.Item.ItemType with
                | Folder -> addFolder parent currentState.NextSortKey logicalPath ignore |> ignore
                | BuildAction buildAction ->
                    let itemInfo = ItemInfo.Create(logicalPath, physicalPath, parent, currentState.NextSortKey)
                    if files.ContainsKey(physicalPath) then
                        logger.Warn(sprintf "%O added twice" physicalPath)
                        files.Remove(physicalPath) |> ignore
                    files.Add(physicalPath, FileItem (itemInfo, buildAction, item.TargetFrameworkIds))
            | _ -> ()

    member x.Write(writer: UnsafeWriter) =
        let writeTargetFrameworkIds ids =
            writer.Write(UnsafeWriter.WriteDelegate(fun writer (value: TargetFrameworkId) ->
                value.Write(writer)), ids)

        writer.Write(projectDirectory)
        writer.Write(projectUniqueName)
        writeTargetFrameworkIds targetFrameworkIds

        let foldersIds = Dictionary<FSharpProjectModelElement, int>()
        let getFolderId el =
            foldersIds.GetOrCreateValue(el, fun () -> foldersIds.Count)

        foldersIds.[Project] <- 0

        iter (fun projectItem ->
            let info = projectItem.ItemInfo
            writer.Write(info.PhysicalPath)
            writer.Write(info.LogicalPath)
            writer.Write(getFolderId info.Parent)
            writer.Write(info.SortKey)

            match projectItem with
            | FileItem (_, buildAction, targetFrameworks) ->
                writer.Write(int FSharpProjectItemType.File)
                writer.Write(buildAction.Value)
                writeTargetFrameworkIds targetFrameworks

            | FolderItem (_, identity) ->
                writer.Write(int FSharpProjectItemType.Folder)
                writer.Write(getFolderId (ProjectItem projectItem))
                writer.Write(identity.Identity))

    member private x.AddItem(path, item: FSharpProjectItem) =
        let path = item.PhysicalPath
        match item with
        | FileItem _ -> files.[path] <- item
        | FolderItem _ -> folders.AddValue(path, item)

    static member Read(reader: UnsafeReader) =
        let projectDirectory = reader.ReadFileSystemPath()
        let projectUniqueName = reader.ReadString()
        let targetFrameworkIds =
            reader.ReadCollection(UnsafeReader.ReadDelegate(TargetFrameworkId.Read), fun _ -> HashSet())

        let logger = Logger.GetLogger<FSharpItemsContainer>()
        let mapping = ProjectMapping(projectDirectory, projectUniqueName, targetFrameworkIds, logger) :> ProjectMapping
        let foldersById = Dictionary<int, FSharpProjectModelElement>()
        foldersById.[0] <- Project

        let itemInfo =
            { PhysicalPath = reader.ReadFileSystemPath()
              LogicalPath = reader.ReadFileSystemPath()
              Parent = foldersById.[reader.ReadInt()]
              SortKey = reader.ReadInt() }

        let item =
            match reader.ReadInt() |> LanguagePrimitives.EnumOfValue with
            | FSharpProjectItemType.File ->
                let (BuildAction buildAction) = reader.ReadString()
                let frameworks = HashSet() // todo: read frameworks
                FileItem(itemInfo, buildAction, frameworks)

            | FSharpProjectItemType.Folder ->
                let id = reader.ReadInt()
                let folderIdentity = { Identity = reader.ReadInt() }
                let item = FolderItem(itemInfo, folderIdentity)
                foldersById.[id] <- ProjectItem item
                item

            | itemType -> sprintf "got item %O" itemType |> failwith

        mapping.AddItem(item)

        mapping

    static member Marshaller =
        { new IUnsafeMarshaller<ProjectMapping> with
            member x.Marshal(writer, value) = value.Write(writer)
            member x.Unmarshal(reader) = ProjectMapping.Read(reader) }

    member x.UpdateFile(oldItemType, oldLocation, BuildAction buildAction, newLocation) =
        match tryGetFile oldLocation with
        | Some (FileItem (info, oldBuildAction, targetFrameworkIds)) ->
            Assertion.Assert(equalsIgnoreCase oldItemType oldBuildAction.Value, "old build action mismatch")

            files.Remove(oldLocation) |> ignore
            files.Add(newLocation, FileItem (info, buildAction, targetFrameworkIds))
            if oldLocation <> newLocation then
                info.LogicalPath <- newLocation
                info.PhysicalPath <- newLocation
        | item -> sprintf "got item %O" item |> failwith

    member x.RemoveFile(itemType, location) =
        let folderRefresher, itemUpdater, refresh = createRefreshers ()
        match tryGetFile location with
        | Some (FileItem _ as item) ->
            removeItem item folderRefresher itemUpdater
            refresh ()
        | item -> sprintf "got item %O" item |> failwith

    member x.UpdateFolder(oldLocation, newLocation) =
        Assertion.Assert(oldLocation.Parent = newLocation.Parent, "oldLocation.Parent = newLocation.Parent")
        let _, itemUpdater, refresh = createRefreshers ()
        renameFolder oldLocation newLocation itemUpdater
        refresh ()

    member x.TryGetProjectItem(viewItem: FSharpViewItem): FSharpProjectItem option =
        let path = viewItem.ProjectItem.Location
        match viewItem with
        | :? FSharpViewFile -> tryGetFile path
        | :? FSharpViewFolder as viewFolder ->
            getFolders path
            |> List.tryFind (function
                | FolderItem (_, id) -> id = viewFolder.Identitiy
                | _ -> false)
        | item -> sprintf "got item %O" item |> failwith

    member x.TryGetProjectItems(path: FileSystemPath): FSharpProjectItem list =
        getItemsForPath path

    member x.AddFile(BuildAction buildAction, physicalPath, logicalPath, relativeToPath, relativeToType) =
        let folderRefresher, itemUpdater, refresh = createRefreshers ()

        let tryGetPossiblyRelativeNodeItem path =
            if isNull path then None else
            tryGetFile path
            |> Option.orElseWith (fun _ ->
                match getFolders path with
                | EmptyFolder _ as item :: [] -> Some item
                | _ -> None)

        let parent, sortKey =
            match tryGetPossiblyRelativeNodeItem relativeToPath, relativeToType with
            | Some relativeItem, Some relativeToType ->

                // Try adjacent item, if its path matches new item path better (i.e. shares a longer common path)
                let relativeItem, relativeToType =
                    match tryGetAdjacentRelativeItem (ProjectItem relativeItem) None relativeToType with
                    | Some (item, relativeToType) when
                            let relativeCommonParent = getCommonParent logicalPath relativeToPath
                            let adjacentCommonParent = getCommonParent logicalPath item.LogicalPath
                            relativeCommonParent.IsPrefixOf(adjacentCommonParent) ->
                        item, changeDirection relativeToType
                    | _ -> relativeItem, relativeToType

                let relativeItemParent =
                    match relativeItem with
                    | FolderItem _ when relativeItem.LogicalPath = relativeToPath -> ProjectItem relativeItem
                    | _ -> relativeItem.Parent

                let parent, sortKey =
                    match relativeItemParent with
                    | ProjectItem item when item.LogicalPath = logicalPath.Parent ->
                        relativeItemParent, getNewRelativeSortKey relativeItem relativeToType
                    | _ ->
                        // The new item is not in the same folder as the relative item.
                        // We should add new folders and split the relative item parent if needed.
                        createFoldersForItem logicalPath relativeItem relativeToType folderRefresher itemUpdater

                moveFollowingItems parent sortKey MoveDirection.Down itemUpdater
                parent, sortKey
            | _ ->
                let parent =
                    logicalPath.GetParentDirectories()
                    |> Seq.takeWhile (fun p -> p <> projectDirectory)
                    |> Seq.rev
                    |> Seq.fold (getOrCreateFolder folderRefresher) Project
                parent, getNewSortKey parent

        let itemInfo = ItemInfo.Create(logicalPath, physicalPath, parent, sortKey)
        files.Add(physicalPath, FileItem(itemInfo, buildAction, targetFrameworkIds))
        tryGetProjectMarkForProject projectUniqueName
        |> Option.iter (fun projectMark -> refresher.SelectItem(projectMark, logicalPath))
        refresh ()

    member x.TryGetRelativeChildPath(modifiedItem, relativeItem, relativeToType) =
        let modifiedNodeItem = getItemsForPath modifiedItem.Location |> Seq.tryHead
        match getRelativeChildPathImpl relativeItem modifiedNodeItem relativeToType with
        | Some (relativeChildItem, relativeToType) when
                relativeChildItem.LogicalPath = modifiedItem.ProjectItem.Location ->

            // When moving files, we remove each file first and then we add it next to the relative item.
            // An item should not be relative to itself as we won't be able to find place to insert after removing.
            // We need to find another item to be relative to.
            match tryGetAdjacentRelativeItem (ProjectItem relativeChildItem) modifiedNodeItem relativeToType with
            | Some (adjacentItem, relativeToType) ->
                  Some (adjacentItem.LogicalPath, changeDirection relativeToType)
            | _ -> 
                // There were no adjacent items in this direction, try the other one.
                let relativeToType = changeDirection relativeToType
                tryGetAdjacentRelativeItem (ProjectItem relativeChildItem) modifiedNodeItem relativeToType
                |> Option.map (fun (item, relativeToType) -> item.LogicalPath, changeDirection relativeToType)

        | Some (item, reltativeToType) -> Some (item.LogicalPath, relativeToType)
        | _ -> None

    member x.GetProjectItemsPaths(targetFrameworkId) =
        let result = List()
        iter (function
            | FileItem (info, buildAction, ids) when ids.Contains(targetFrameworkId) ->
                result.Add((info.PhysicalPath, buildAction))
            | _ -> ())
        result.ToArray()

    member x.Dump(writer: TextWriter) =
        let rec dump (parent: FSharpProjectModelElement) ident =
            for item in getChildren parent do
                writer.WriteLine(sprintf "%s%d:%O" (String(' ', ident * 2)) item.SortKey item)
                dump (ProjectItem item) (ident + 1)
        dump Project 0

        for targetFrameworkId in targetFrameworkIds do
            writer.WriteLine()
            writer.WriteLine(targetFrameworkId)
            x.GetProjectItemsPaths(targetFrameworkId)
            |> Array.iter (fun ((UnixSeparators path), _) -> writer.WriteLine(path))
            writer.WriteLine()

    member x.DumpToString() =
        let writer = new StringWriter()
        x.Dump(writer)
        writer.ToString()


type FSharpProjectModelElement =
    | Project
    | ProjectItem of FSharpProjectItem


[<ReferenceEquality>]
type FSharpProjectItem =
    | FileItem of ItemInfo * BuildAction * ISet<TargetFrameworkId>
    | FolderItem of ItemInfo * FSharpViewFolderIdentity 

    member x.ItemInfo: ItemInfo =
        match x with
        | FileItem (info, _, _)
        | FolderItem (info, _) -> info

    member x.SortKey = x.ItemInfo.SortKey
    member x.Parent  = x.ItemInfo.Parent
    member x.PhysicalPath: FileSystemPath = x.ItemInfo.PhysicalPath
    member x.LogicalPath: FileSystemPath = x.ItemInfo.LogicalPath

    override x.ToString() =
        let name =
            match x with
            | FolderItem (_, id) as folderItem -> sprintf "%s[%d]" x.LogicalPath.Name id.Identity
            | FileItem (_, buildAction, _) when
                    not (buildAction.IsCompile()) -> sprintf "%s (%O)" x.LogicalPath.Name buildAction
            | _ -> x.LogicalPath.Name
        if x.PhysicalPath = x.LogicalPath then name
        else
            let (UnixSeparators path) = x.PhysicalPath
            sprintf "%s (from %s)" name path

[<RequireQualifiedAccess>]
type FSharpProjectItemType =
    | File = 0
    | Folder = 1


type ItemInfo =
    { mutable LogicalPath: FileSystemPath
      mutable PhysicalPath: FileSystemPath
      mutable Parent: FSharpProjectModelElement
      mutable SortKey: int }

    static member Create(logicalPath, physicalPath, parent, sortKey) =
        { LogicalPath = logicalPath; PhysicalPath = physicalPath; Parent = parent; SortKey = sortKey }

    override x.ToString() = x.LogicalPath.Name


[<RequireQualifiedAccess>]
type MoveDirection =
    | Up
    | Down


type State =
    { Path: FileSystemPath
      Folder: FSharpProjectModelElement
      mutable NextSortKey: int }

    static member Create(path, folder) =
        { Path = path; Folder = folder; NextSortKey = 0 }


type RdProjectItemWithTargetFrameworks =
    { Item: RdProjectItem
      TargetFrameworkIds: HashSet<TargetFrameworkId> }


type IFSharpItemsContainerRefresher =
    /// Refreshes the project structure for a project.
    abstract member Refresh: IProjectMark * isOnProjectLoad: bool -> unit

    /// Refreshes the project structure for a folder in a project.
    abstract member Refresh: IProjectMark * folder: FileSystemPath * identity: FSharpViewFolderIdentity -> unit

    /// Updates presentation (i.e. changes sort key) for a file.
    abstract member Update: IProjectMark * file: FileSystemPath -> unit

    /// Updates presentation (i.e. changes sort key) for a folder.
    abstract member Update: IProjectMark * folder: FileSystemPath * identity: FSharpViewFolderIdentity -> unit 

    /// Used on changes we currenlty cannot process, e.g. Compile -> CompileBefore build action change.
    abstract member ReloadProject: IProjectMark -> unit

    /// Selects an item after a project structure change that could make an item parent folder collapse.
    abstract member SelectItem: IProjectMark * FileSystemPath -> unit


[<SolutionInstanceComponent>]
type FSharpItemsContainerRefresher(lifetime: Lifetime, solution: ISolution, viewHost: ProjectModelViewHost) =

    let tryGetProject projectMark =
        solution.GetProjectByMark(projectMark) |> Option.ofObj

    let refresh projectMark getFolders =
        use lock = solution.Locks.UsingReadLock()
        tryGetProject projectMark
        |> Option.iter (fun project ->
            for projectFolder in getFolders project do
                solution.Locks.QueueReadLock(lifetime, "Refresh View", fun _ ->
                    match solution.TryGetComponent<ProjectModelAppender>() with
                    | null -> ()
                    | appender -> appender.Refresh(projectFolder)))

    let update projectMark path viewItemCtor =
        use lock = solution.Locks.UsingReadLock()
        tryGetProject projectMark
        |> Option.iter (fun project ->
            for viewItem in project.FindProjectItemsByLocation(path) |> Seq.choose viewItemCtor do
                solution.Locks.QueueReadLock(lifetime, "Refresh View", fun _ ->
                    if solution.GetComponent<FSharpItemsContainer>().IsValid(viewItem) then
                        viewHost.UpdateItemIfExists(viewItem)))

    interface IFSharpItemsContainerRefresher with
        member x.Refresh(projectMark, isOnProjectLoad) =
            refresh projectMark (fun project -> [project])

        // todo: single identity
        member x.Refresh(projectMark, folder, folderIdentity) =
            refresh projectMark (fun project -> project.FindProjectItemsByLocation(folder).OfType<IProjectFolder>()) 
    
        member x.Update(projectMark, path) =
            update projectMark path (function | ProjectFile x -> Some (FSharpViewFile(x)) | _ -> None)

        member x.Update(projectMark, path, id) =
            update projectMark path (function | ProjectFolder x -> Some (FSharpViewFolder(x, id)) | _ -> None)

        member x.ReloadProject(projectMark) =
            let opName = sprintf "Reload %O after FSharpItemsContainer change" projectMark
            solution.Locks.QueueReadLock(lifetime, opName, fun _ ->
                solution.ProjectsHostContainer().GetComponent<ISolutionHost>().ReloadProject(projectMark))

        // todo: select item when moving to empty folder 
        member x.SelectItem(projectMark, filePath) =
            let opName = sprintf "Select %O after FSharpItemsContainer change" filePath
            solution.Locks.QueueReadLock(lifetime, opName, fun _ ->
                tryGetProject projectMark
                |> Option.bind (fun project ->
                    project.FindProjectItemsByLocation(filePath).OfType<IProjectFile>() |> Seq.tryHead)
                |> Option.filter (fun projectFile -> projectFile.IsValid())
                |> Option.iter (fun projectFile ->

                let navigationManager = NavigationManager.GetInstance(solution)
                ignore (Lifetimes.Using(fun lifetime ->
                    let points =
                        navigationManager
                            .GetNavigationPoints<ISolutionExplorerNavigationProvider, IProjectItem>(projectFile)
                    let solutionExplorerDataContext = solution.GetComponent<DataContexts>().CreateOnSelection(lifetime)
                    let caption = RichText("Navigate to Solution Explorer")
                    let options = NavigationOptions.FromDataContext(solutionExplorerDataContext, caption, true)
                    navigationManager.Navigate(points, options)))))


// todo: extract IProjectElementHolder interface back in ReSharperHost
[<AbstractClass; AllowNullLiteral>]
type FSharpViewItem(item: IProjectItem) =
    inherit ProjectElementHolder(item)

    member x.ProjectItem: IProjectItem = item
    member x.Location = item.Location


type FSharpViewFile(file: IProjectFile) =
    inherit FSharpViewItem(file)

    member x.ProjectFile = file

    override x.Equals(other: obj) =
        match other with
        | null -> false
        | :? FSharpViewFile as file ->
            obj.ReferenceEquals(x, other) ||
            x.ProjectFile.Equals(file.ProjectFile)
        | _ -> false

    override x.GetHashCode() =
        x.ProjectFile.GetHashCode()

[<AllowNullLiteral>]
type FSharpViewFolder(folder: IProjectFolder, identity: FSharpViewFolderIdentity) =
    inherit FSharpViewItem(folder)

    member x.ProjectFolder = folder
    member x.Identitiy = identity
    override x.ToString() = sprintf "%s[%O]" folder.Name identity

    override x.Equals(other: obj) =
        match other with
        | null -> false
        | :? FSharpViewFolder as folder ->
            obj.ReferenceEquals(x, other) ||
            x.ProjectFolder.Equals(folder.ProjectFolder) && x.Identitiy = folder.Identitiy
        | _ -> false

    override x.GetHashCode() =
        x.ProjectFolder.GetHashCode() * 397 ^^^ x.Identitiy.GetHashCode()


type FSharpViewFolderIdentity =
    { mutable Identity: int }

    override x.ToString() = x.Identity.ToString()


[<SolutionFeaturePart>]
type FSharpItemModificationContextProvider(container: IFSharpItemsContainer) =
    inherit ItemModificationContextProvider()

    override x.IsApplicable(project) = project.IsFSharp

    override x.CreateModificationContext(modifiedItem, relativeItem, relativeToType) =
        let context =
            match modifiedItem, relativeItem with
            | (:? FSharpViewItem as modifiedViewItem), (:? FSharpViewItem as relativeViewItem) ->
                x.CreateModificationContext(modifiedViewItem, relativeViewItem, relativeToType)
            | _ -> None
        match context with
        | Some context -> context :> _
        | _ -> base.CreateModificationContext(modifiedItem, relativeItem, relativeToType)

    member x.CreateModificationContext(modifiedViewItem, (relativeViewItem: FSharpViewItem), relativeToType) =
        let project = relativeViewItem.ProjectItem.GetProject().NotNull()
        container.TryGetRelativeChildPath(project.GetProjectMark(), modifiedViewItem, relativeViewItem, relativeToType)
        |> Option.map (fun (path, relativeToType) ->
            let relativeProjectItem = project.FindProjectItemsByLocation(path).First()
            RiderItemModificationContext(RelativeTo(relativeProjectItem, relativeToType)))


[<ShellComponent>]
type FSharpModificationSettingsProvider() =
    interface IMsBuildModificationSettingsProvider with
        member x.SmartModificationsFilter = ["fsproj"] :> _
