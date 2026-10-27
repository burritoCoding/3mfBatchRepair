# 3mfBatchRepair

Repair broken STL, OBJ and 3MF files in bulk with the repair engine built into Windows (the one behind 3D Builder's Repair button).

- Repairs whole folders, several files at a time.
- Checks everything first and only repairs what's broken, quickest first, with a time estimate.
- Only replaces an original when the repair comes out clean. The old file goes to the Recycle Bin.
- Adds **Repair with 3mfBatchRepair** to Explorer's right-click menu.
- Stop whenever you like. Finished files are kept and nothing is left half-written.
- Comes with **3mfMeshSplit**, which splits a plate of parts into one STL per part.

## Platform support

Windows 10 and 11 only. The repairs are done by Windows' own 3D printing engine (`Windows.Graphics.Printing3D`), which only exists on those versions. There are no plans for Mac or Linux support.

## Install

Run `3mfBatchRepair-setup.exe`. It installs for your user only, with no admin rights needed. It isn't code-signed, so Windows may warn you: choose **More info → Run anyway**.

You can also just copy the exes anywhere and run them.

## Use

**Window:** open 3mfBatchRepair, add files or folders (or drag them in), and press **Start**.

**Command line:**

```
3mfBatchRepair "D:\STLs"                     repair in place
3mfBatchRepair "D:\STLs" -o "D:\Fixed"       write repaired copies to another folder
3mfBatchRepair "D:\STLs" --check             only report problems
```

**3mfMeshSplit** (window or command line):

```
3mfMeshSplit "plate.stl"                     -> plate_parts\plate_01.stl, plate_02.stl, ...
3mfMeshSplit "plate.stl" --dedupe            identical parts once, named "(print 4x)"
```

Supports and other overlapping pieces stay with their part. Run either tool with `--help` for every option.

## Good to know

- Big, badly broken models can take 10 minutes or more each. A repair often sits at 86% while the engine fuses supports into the model. It hasn't hung.
- Repairs keep geometry only, so OBJs with textures and 3MF files aren't repaired in place. Save them to another folder instead.
- Each run is logged to `3mfBatchRepair-log.csv`, in the output folder or in `%LOCALAPPDATA%\3mfBatchRepair`.

## Build

Run `installer\build-installer.cmd` to build the tools and the installer. It uses the C# compiler that ships with Windows and needs the Windows 10 SDK.

## Credits

Icon: "3d-hammer" from [game-icons.net](https://game-icons.net), [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/).
