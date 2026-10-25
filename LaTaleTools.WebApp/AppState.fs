module LaTaleTools.WebApp.AppState

open System
open System.IO
open System.IO.MemoryMappedFiles
open FSharpPlus
open LaTaleTools.Library.Spf
open LaTaleTools.Library.VirtualFs
open Microsoft.Extensions.Logging

type public SpfHandle =
    { PhysicalPath: string // on disk
      MemoryMappedFile: MemoryMappedFile
    }

type public AppState =
    { VirtualFs: VirtualFsState
      SpfViews: Map<SourceFile, SpfHandle>
    }

    interface IDisposable with
        member this.Dispose() =
            for KeyValue (_, handle) in this.SpfViews do
                handle.MemoryMappedFile.Dispose()

type public InArchiveFileHandle =
    { SpfHandle: SpfHandle
      Metadata: FileEntryMetadata
    }

let findHandleInArchive (inArchivePath: string) (appState: AppState) =
    let archivePath = ArchivePath inArchivePath
    monad' {
        let! FileEntry (src, metadata) = appState.VirtualFs.FileMapping.TryFind archivePath
        let! spfHandle = appState.SpfViews.TryFind src
        return { SpfHandle = spfHandle
                 Metadata = metadata
               }
    }

let public openStreamInArchive (inArchivePath: string) (appState: AppState) =
    findHandleInArchive inArchivePath appState
    |> Option.map (fun handle ->
        handle.SpfHandle.MemoryMappedFile.CreateViewStream(
            handle.Metadata.ContentPosition,
            handle.Metadata.ContentSize,
            MemoryMappedFileAccess.Read
            ) :> Stream
    )

let public openViewInArchive (inArchivePath: string) (appState: AppState) =
    findHandleInArchive inArchivePath appState
    |> Option.map (fun handle ->
        handle.SpfHandle.MemoryMappedFile.CreateViewAccessor(
            handle.Metadata.ContentPosition,
            handle.Metadata.ContentSize,
            MemoryMappedFileAccess.Read
       )
    )

let openSpf (path: string): SpfHandle =
    if not <| File.Exists path then
        invalidArg (nameof path) $"File does not exist: %s{path}"

    let path = Path.GetFullPath path
    let mmf = MemoryMappedFile.CreateFromFile (path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read)
    { PhysicalPath = path
      MemoryMappedFile = mmf
    }

let listSpfFiles (dirPath: string) =
    Directory.GetFiles dirPath
    |> List.ofArray
    |> List.filter _.ToLower().EndsWith("spf")


let internal buildAppStateFromFiles (logger: ILogger) openArchive spfFiles: AppState =
    let actionLogger = Logging.prepareActionLogger logger

    let readMetadataLogged path view =
        use _ = actionLogger $"ReadingSpf[{path}]"
        readMetadata view

    // Retain ownership until the complete state (including the virtual filesystem) is built.
    let spfHandles = ResizeArray<SpfHandle>()
    try
        for path in spfFiles do
            spfHandles.Add(openArchive path)

        let inArchiveFiles =
            spfHandles
            |> Seq.collect (fun spf ->
                use view = spf.MemoryMappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read)
                let metadata = readMetadataLogged spf.PhysicalPath view
                metadata |> List.map (fun m -> FileEntry (SourceFile spf.PhysicalPath, m)))
            |> List.ofSeq

        let spfViews =
            spfHandles
            |> Seq.map (fun handle -> (SourceFile handle.PhysicalPath, handle))
            |> Map.ofSeq

        { VirtualFs = buildFromFileEntries inArchiveFiles
          SpfViews = spfViews }
    with _ ->
        for handle in spfHandles do
            handle.MemoryMappedFile.Dispose()
        reraise ()

let public buildAppState (logger: ILogger) (dirOrFilePath: string): AppState =
    let spfFiles =
        if File.Exists dirOrFilePath then
            [dirOrFilePath]
        elif Directory.Exists dirOrFilePath then
            listSpfFiles dirOrFilePath
        else
            invalidArg (nameof dirOrFilePath) $"Invalid path: %s{dirOrFilePath}"

    buildAppStateFromFiles logger openSpf spfFiles
