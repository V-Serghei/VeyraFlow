# Repository Explorer: Limited File Operations Mode

This screen is not a full-featured file manager and should not be positioned as a replacement for Windows Explorer. The auxiliary file operations exist solely for convenience when working inside an already-open tracked repository folder.

## Show All Files

By default the list shows indexed repository items — files and folders already known to VeyraFlow through the normal scan and snapshot history mechanism.

The **Show All Files** toggle additionally reads the current filesystem of the selected folder and surfaces items that have not yet entered the index:

- **Tracked** — the item already exists in the local repository index.
- **Modified** — an indexed file exists on disk, but its size or modification time differs from the saved record.
- **Deleted** — the file is in the index but is absent from disk.
- **New** — the file is found on disk, its extension is in the tracked formats, but it has not yet entered the index.
- **Untracked** — the file is found on disk but its extension is not in the repository's tracked formats.

Untracked files are visually dimmed and are not added to snapshots automatically. They will appear in a snapshot only if the format is enabled in repository settings and the normal scan/snapshot mechanism processes the change.

VeyraFlow's internal restore folders, such as `.veyra-restores` and `.veyra-rollback-backups`, are not shown in this mode to avoid mixing user files with internal restore outputs.

User-defined exclusion patterns from repository settings remain the responsibility of the normal scanner/snapshot mechanism. The explorer prototype does not attempt to fully replicate the scanner's glob matching for every local file, so such files may appear as local items but should not enter snapshots if the scanner excludes them.

## Available Operations

The folder-tree context menu provides:

- open a folder in the file list
- reveal the folder in Windows Explorer
- create a new folder
- refresh the tree / current folder

The file-list item context menu provides:

- open the file or folder
- reveal the item in Windows Explorer
- copy the item to another folder within this repository
- move the item to another folder within this repository
- refresh the current folder

Copy and move operations do not silently overwrite existing files. If the target folder already contains an item with the same name, the operation is cancelled. Moving outside the repository is not performed without a separate confirmation in the current prototype.

## Snapshot Behavior

File operations do not create a snapshot automatically. They modify the normal filesystem inside the repository, after which the existing file watcher or a manual scan reflects the changes in pending changes. The user creates a snapshot when ready to commit the state.

## Prototype Limitations

Drag-and-drop is not implemented: it is safer to keep operations in the context menu so that accidental drags do not silently restructure the repository.

File deletion from the UI is not included. Deletion requires a separate confirmation flow and clear display of the consequences in history.

Icon/Grid view uses lightweight icons and already-loaded metadata. Heavy previews for all files at once are not performed.
