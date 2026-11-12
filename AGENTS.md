# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` says what the program does,
`AVAPageStream_Spec.md` says what it is meant to become, and this says how the code is put together and what keeps
it working.

## What this is

AVAPageStream is a frame-based desktop publishing program in C# on .NET 10 with an Avalonia 12 window: pages and
master pages, text frames whose stories flow through linked chains of frames and columns, pictures and shapes,
rulers and guides, undo, `.avps` files, and PDF and PNG export drawn by the same renderer as the screen.

## Commands

```bash
dotnet build AVAPageStream.slnx
dotnet test AVAPageStream.slnx                 # headless; the window tests run on Avalonia's headless platform
dotnet run --project src/AVAPageStream         # the program
AVAPAGESTREAM_SHOTS=/some/folder dotnet test AVAPageStream.slnx
                                               # also saves the README's screenshots and the sample PDF there
python3 tools/make_icon.py                     # redraws Assets/AVAPageStream.png and .ico
```

## Layout

- `src/AVAPageStream.Core/`: everything but the window, with no Avalonia in it.
  - `Units.cs` (points, picas, inches, mm, cm; reading typed measures), `Colours.cs`, `Box.cs` (`Pt`, `Box`).
  - `Model.cs`: `Setup`, `Sheet` (a `Page` or `MasterPage`), `SheetRef`, `Guide`, and the objects: `PageObject` and
    `RectangleObject`, `EllipseObject`, `LineObject`, `TextFrame`, `PictureObject` (JSON-polymorphic by `kind`).
  - `Document.cs`: `Document` (setup, styles, swatches, stories, masters, pages) and `ImageStore` (pictures by SHA-256,
    shared by every undo state).
  - `Story.cs`: `Story` with its `CharFormat` runs and `ParaFormat` per paragraph, and `ParagraphStyle`.
  - `TextFlow.cs`: `ITextMeasurer`, and `TextFlow.Layout`, which sets a story through its `Area`s (each column of each
    frame in its chain) as `Line`s of `Piece`s; `TextLayout` answers caret positions, hit tests and selection boxes.
  - `Chains.cs` (a story's frames and areas; linking and unlinking), `Editor.cs` (the document, every change, and
    undo), `TextNav.cs` (words), `Snapping.cs`, `DocumentFile.cs` (the ZIP file),
    `PageRanges.cs` (reading "1-3, 5"), `AppSettings.cs`.
- `src/AVAPageStream/`: the Avalonia program, built in code rather than XAML.
  - `PageRenderer.cs`: `Layouts` (each story's layout, kept until its text, formats or areas change),
    `PageRenderer` (draws a page and its master in points) and `PageVisual` (one page as a control, for export).
  - `PageView.cs`: the pasteboard, rulers, tools, selection, handles, guides, snapping, cropping and in-place text
    editing; `PageScroller.cs` puts scroll bars round it.
  - `Panels.cs` (`SidePanel`: the Object, Text, Pages and Document tabs), `MainWindow.cs` (menus, tool bar, toolbox,
    status bar, files, clipboard, keys), `Export.cs` (PDF through SkiaSharp, PNG), `FontSupport.cs` (the bundled
    fonts and `FontMeasurer`), `Look.cs`, `Dialogs.cs` (`AskDialog`, `ExportDialog`).
  - `Assets/Fonts/`: Liberation Serif and Sans, with their licence.
- `tests/AVAPageStream.Tests/`: xUnit v3 with Avalonia.Headless.XUnit. `Core/` tests the library with
  `FixedMeasurer` (every character half its size wide, so layouts are easy sums); `WindowTests` drives the window
  with the mouse and keys and real fonts; `Sample.cs` builds the newsletter in the screenshots.

## Rules that keep it working

- **Nothing in Core touches a window.** Anything that can be tested without one goes in Core, with tests.
- **Every change goes through `Editor`.** `Change` makes one undo step (consecutive changes with the same `merge` key,
  such as typing in one story, make one between them); `TryChange` for changes that may refuse; `Begin`, `Preview`
  and `Commit` (or `Cancel`) for drags. Never change `Editor.Doc` outside those, or undo will miss it.
- **Hold ids and `SheetRef`s, not objects.** Undo swaps in another copy of the document, so a `PageObject` or `Sheet`
  held across a change may be stale. Look them up again with `Doc.Find(id)` or `SheetRef.Resolve`.
- **Stories stay well formed.** Runs cover every character plus one for the story's end, never empty, never two equal
  neighbours; one `ParaFormat` per paragraph. Only `Story`'s own methods change them (`IsWellFormed` checks it, and
  `DocumentFile` repairs files that break it).
- **One renderer.** The screen, PDF and PNG all draw through `PageRenderer`, and text is measured and drawn with the
  same `FormattedText`, so they match. Screen-only things (guides, frame edges, handles, the caret) are drawn by
  `PageView` on top, never in the renderer.
- **Layouts are cached by what they depend on.** `Layouts.Of` lays a story out again only when its text, runs,
  paragraph formats or areas differ from last time; a test counts layouts (`Layouts.LaidOut`). Anything new that
  changes how a story sets (text wrap, say) must join that comparison.
- **Every question the window asks goes through a replaceable function** (`Ask`, `PickOpen`, `PickSave`,
  `AskExport`, `PageView.PickImage`), so tests answer it; the test window fails any it didn't expect.
- **Positions are points**, from the top-left of the page an object is on, rounded to 1/100 pt where the mouse sets them.
- **Subclassed controls need `StyleKeyOverride`** (as `SidePanel` has) or the theme gives them no template.
- **PDF metadata starts from `SKDocumentPdfMetadata.Default`**: a bare initialiser zeroes the encoding quality, which
  turns every picture into the worst JPEG. The export test checks for it.
- **Errors say what to do**, in the status bar, never as a bare exception.
- **Warnings are errors** (`TreatWarningsAsErrors`), and nullable reference types are on.
