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
./make-guide.sh                                # rebuilds USERGUIDE.pdf from USERGUIDE.md (Tools/GuideBuilder)
```

## Layout

- `src/AVAPageStream.Core/`: everything but the window, with no Avalonia in it.
  - `Units.cs` (points, picas, inches, mm, cm; reading typed measures), `Colours.cs`, `Box.cs` (`Pt`, `Box`).
  - `Model.cs`: `Setup`, `Sheet` (a `Page`, a `MasterPage`, or a master's left `MasterSide`), `SheetRef`, `Guide`,
    and the objects: `PageObject` and `RectangleObject`, `EllipseObject`, `LineObject`, `TextFrame`, `PictureObject`,
    `PathObject` (with `PathNode`s) and `GroupObject` (JSON-polymorphic by `kind`).
  - `Shapes.cs`: rotation (`ToLocal`, `ToPage`, `Corners`, `Extent`), walking groups (`Flatten`, `ContainerOf`,
    `GroupsAround`), a path flattened to points, and the `Keep` (box or outline) a wrapping object makes.
  - `Document.cs`: `Document` (setup, styles, swatches, stories, masters, pages) and `ImageStore` (pictures by SHA-256,
    shared by every undo state).
  - `Story.cs`: `Story` with its `CharFormat` runs and `ParaFormat` per paragraph, and `ParagraphStyle`.
  - `TextFlow.cs`: `ITextMeasurer`, and `TextFlow.Layout`, which sets a story through its `Area`s (each column of each
    frame in its chain, with the wrap boxes it keeps out of) as `Line`s of `Piece`s; `TextLayout` answers caret
    positions, hit tests and selection boxes. `Fields` and `FieldContext`: the page-number and page-count characters.
  - `Footnotes.cs` (numbering, room at the foot, `Tidy`), `PathText.cs` (text along a path), `PrintMarks.cs` (crop
    marks), `Links.cs` (linked pictures), `Recovery.cs` (autosave copies), `Preflight.cs` (with `Fitting`), `Package.cs`, `PdfExtras.cs` (bookmarks, links,
    and the update that adds the outline and boxes to a PDF).
  - `Chains.cs` (a story's frames and areas; linking and unlinking), `Editor.cs` (the document, every change, and
    undo), `TextNav.cs` (words), `Snapping.cs`, `DocumentFile.cs` (the ZIP file),
    `PageRanges.cs` (reading "1-3, 5"), `Search.cs` (find through the stories in reading order), `TextImport.cs`
    (plain text and Markdown to placed paragraphs), `Tabs.cs` (tab stops), `Hyphenator.cs` (Liang's algorithm over
    the patterns embedded from `Hyphenation/`), `Contents.cs` (a table of contents), `Imposition.cs` (booklet
    order), `Templates.cs` (the built-in templates and .avpt files), `Speller.cs` (the SCOWL lists in `Spelling/`),
    `Eps.cs` and `Ghostscript.cs` (EPS size, preview, and rendering when gs is there), `Tiff.cs` (baseline TIFF, for
    the previews in DOS binary EPS headers), `PostScript/` (the EPS interpreter), `Tables.cs` (rows, columns,
    fitting), `AppSettings.cs`.
- `src/AVAPageStream/`: the Avalonia program, built in code rather than XAML.
  - `EpsRender.cs`: an EPS drawing as a Skia picture (and the JPEG decoder the interpreter is given).
  - `PageRenderer.cs`: `Layouts` (each story's layout, kept until its text, formats or areas change),
    `PageRenderer` (draws a page and its master in points) and `PageVisual` (one page as a control, for export).
  - `PageView.cs`: the pasteboard, rulers, tools, selection, handles, guides, snapping, cropping and in-place text
    editing; `PageScroller.cs` puts scroll bars round it.
  - `StoryEditor.cs`: a story as plain text in a window of its own, kept in step with the page both ways.
  - `SpellingWindow.cs`: the spelling check, word by word. `PreflightWindow.cs`: the list of problems.
  - `Pictures.cs`: pictures loaded once as a bitmap, an SVG
    (a Skia picture, drawn by `SvgDraw` straight onto the canvas so it stays vector) or an EPS.
  - `Panels.cs` (`SidePanel`: the Object, Text, Pages and Document tabs), `MainWindow.cs` (menus, tool bar, toolbox,
    status bar, files, clipboard, keys), `Export.cs` (PDF and booklets through SkiaSharp, PNG), `Printing.cs` (lp, or Windows' Print action), `FontSupport.cs` (the bundled
    fonts and `FontMeasurer`), `Look.cs`, `Dialogs.cs` (`AskDialog`, `ExportDialog`).
  - `Assets/Fonts/`: Liberation Serif and Sans, with their licence.
- `tests/AVAPageStream.Tests/`: xUnit v3 with Avalonia.Headless.XUnit. `Core/` tests the library with
  `FixedMeasurer` (every character half its size wide, so layouts are easy sums); `WindowTests` drives the window
  with the mouse and keys and real fonts; `Sample.cs` builds the newsletter in the screenshots.

- `USERGUIDE.md` is the user guide (`USERGUIDE.pdf` is made from it by `make-guide.sh`); its pictures in
  `docs/images/guide` come from the `Guide_` tests in `GuideShots.cs`. `Tools/GuideBuilder` is no part of the
  solution or the program.

## Rules that keep it working

- **A change to what the program does wants a line in `USERGUIDE.md` and the README**, its picture retaken if it
  shows, and `./make-guide.sh` run again.

- **Nothing in Core touches a window.** Anything that can be tested without one goes in Core, with tests.
- **Every change goes through `Editor`.** `Change` makes one undo step (consecutive changes with the same `merge` key,
  such as typing in one story, make one between them); `TryChange` for changes that may refuse; `Begin`, `Preview`
  and `Commit` (or `Cancel`) for drags. Never change `Editor.Doc` outside those, or undo will miss it.
- **Move and resize through `Bounds`.** Setting it calls the virtual `SetBounds`, which a group overrides to carry
  its children and a path its points; setting `X`, `Y`, `W` or `H` directly leaves them behind. After changing
  something inside a group, `Document.Refit(id)` brings the groups round it back to size.
- **Walk objects with `Sheet.All` (or `Shapes.Flatten`)**, never `Sheet.Objects` alone, unless you mean only the top
  level (the selection and hit tests do): frames inside groups are frames too.
- **Turned objects work in their own frame.** Hit tests, resizing, text clicks and text overlays turn page points
  with `Shapes.ToLocal`/`ToPage`; anything that needs where an object really is uses `Shapes.Extent`.
- **In a facing document, a sheet's guides come from `Document.GuidesFor(sheet)`** (its master's own margins and
  columns, mirrored on left-hand sheets), and a page's master from `Document.MasterFor(page)` (a left page gets the
  master's left side). `PageView` draws a spread through `_drawOffset`; `ToView`/`ToPage` are relative to the page
  being worked on.
- **A table's cells are stories with `Table` set.** Their `Frames` is the table's id, and their area's id is
  `table#cell`: map an area to its object with `Chains.ObjectOf`, and find text under a point with
  `PageView.TextAt`, which knows frames and cells. Deleting or copying a table must take its cell stories with it.
- **The word lists and hyphenation patterns are other people's work with notices** that must stay with them
  (`Spelling/SCOWL-Copyright.txt`, the header of `Hyphenation/*.txt`). Svg.Skia must stay on a release built on the
  same SkiaSharp as Avalonia (5.1.1 for 3.119).
- **Colours may be swatch references (`@Name`, or `@Name 40%` for a tint).** Draw and show them through `Colours.Resolve`; never compare a
  stored colour with a value.
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
- **Text wrap affects the text under it.** `Chains.Areas` gives a frame the wrap boxes of objects drawn above it on
  its sheet and, on a page, every wrapping object on the master. Beside wrap, several `Line`s can share a baseline
  (one per stretch of room), so anything walking lines by index (up and down, hit tests) must allow for that.
- **Fields are characters, shown through the layout.** Story text holds `Fields.PageNumber`/`PageCount`; anything that
  shows or measures set text goes through `TextLayout.Display` (or `Fields.Expand`), never `Story.Slice` directly. A
  master's story is laid out once per page it is drawn under (`Layouts.Of(doc, id, page)`).
- **Draw and hit-test through `Document.Ordered`**, which sorts a sheet's objects by layer (stably, so their own
  order holds within a layer); a hidden layer is not drawn or wrapped round, a locked one not picked, and one that
  doesn't print is drawn only on screen. New objects go on `Editor.CurrentLayer`.
- **Character styles hold only what they change** (`CharOverrides`), and a run names its style in
  `CharFormat.CharStyle`; applying a paragraph style lays the run's character style over it again. Page labels come
  from `Document.PageLabel`, never from the index, and `FieldContext.Label` carries them into fields.
- **Keeping text together sets paragraphs again.** `TextFlow.Layout` snapshots its state before each paragraph and,
  when one breaks a keep rule, puts the state back and sets it again with a plan (start in the next area, or break
  after so many lines), at most twice per paragraph so it always ends.
- **Footnotes are a reference character and a story.** The reference (`Fields.Footnote`) is a run of its own whose
  `CharFormat.Note` names the note's story, which has `NoteOf` set and starts with `Fields.NoteNumber`. `TextFlow.Layout`
  numbers them and, given note heights, keeps room for each at the foot of its reference's area (`TextLayout.Notes`);
  `Layouts.Of` sets a note in that box. `Footnotes.Tidy` runs after every `Editor.Change` to keep references and notes
  matched: never fix them up by hand.
- **A table that runs on keeps everything in its first frame.** The frames after it (`Continues` set, linked by
  `Next`) have no rows or cells of their own; `Tables.Place` says which frame shows each row and where. A cell's area
  id names the frame showing it, so `Chains.ObjectOf` finds the right frame; repeated header rows are drawn from
  their cells in the first frame, moved. Change a table through its first frame (`Tables.Head`).
- **Text on a path** is a story with `OnPath` set and no frames, drawn by `PathText.Place`. `Layouts.Of` gives it a
  layout of one long line in a far-off area named after the path, for moving the caret only: hit tests
  (`PageView.PathIndexAt`), the caret, selection and misspelling marks come from the letters on the path. Footnotes
  and path stories are in `Search.StoryOrder`, so find and spelling reach them, and `PageView.Reveal` edits them on
  the page.
- **Linked pictures** (`PictureObject.Linked`) aren't written into the `.avps`; `DocumentFile.Read` reads them from
  `Source` or beside the document, and `Editor.UpdateLinks` brings them up to date as one undo step.
- **Autosave** copies go in `Recovery/` beside the settings file, one per window (`MainWindow.RecoveryId`), and go
  when the document is saved, closed or replaced.
- **Preflight is Core** (`Preflight.Check`), given what only the program knows: pictures' pixels
  (`PageRenderer.PixelsOf`) and fonts' states (`FontSupport.State`, which reads embedding rights from the OS/2
  table). `Package.Make` works on a clone, so the document open is never relinked.
- **A PDF's extras**: `PdfExtras.Bookmarks` (the heading styles, `OutlineStyles`) and `Links` are drawn as SkiaSharp
  annotations while each page is written; then `PdfExtras.Finish` appends an update to the file with the outline and
  the trim and bleed boxes. It relies on SkiaSharp's plain layout (a classic xref table, no object streams) and leaves
  the file alone if it doesn't find it.
- **EPS is drawn by our own PostScript interpreter** (`Core/PostScript`: `Scanner`, `Interpreter` and its
  `Language`, `Graphics`, `Images` and `Shading` parts). It paints into a display list (`PsFill`, `PsStroke`,
  `PsImage`, `PsShading`, each with its clips) that `EpsRender` records as a Skia picture, drawn like an SVG.
  Anything it doesn't do throws `PsError("unsupported")`, and `Eps.Draw` returns null so Ghostscript or the
  preview take over: never draw half an EPS. Filters read lazily (their data often comes after the image
  dictionary). The sample EPS files in `tests/.../Samples/eps` come with the PNG of the same picture to compare
  with; add one for each new producer or feature. Text: `Fonts.cs` (operators and showing), `TrueType.cs`
  (Type 42), `Type1.cs` (eexec and charstrings), `Encodings.cs`; printer fonts come from the window through
  `Interpreter.SystemGlyphs` (Liberation, by `EpsRender`), and a test gives its own with `Glyphs` (an interpreter's
  hooks are copied from the static ones when it is made, so tests running at once don't meet). Composite and CID
  fonts are in `Cid.cs`, CFF in `Cff.cs` and `FontSet.cs`, patterns and meshes in `Meshes.cs`, CIE colour in
  `Cie.cs`; `Inflate.cs` is a zlib inflater that stops at its stream's end (for Flate data in the program).
- **CMYK PDFs** are made by `PdfColour` rewriting the finished PDF (read by `PdfFile`, which also writes the update):
  `rg`/`RG` in content streams become `k`/`K` or a spot colour's `scn`, Flate RGB pictures and gradients' functions
  are converted. It relies on SkiaSharp's plain PDF, as `PdfExtras.Finish` does.
- **Effects** (`PageObject.Gradient`, `Opacity`, `FillOpacity`, `StrokeOpacity`, `Blend`, `Shadow`, `Corners`,
  in Core's `Effects.cs`) are drawn by the window's `Effects.cs`: an object with effects is wrapped by two custom
  draw operations, one doing `SaveLayer` (opacity, blend, shadow filter) and one `Restore`, around Avalonia's own
  drawing of it. Their bounds are huge on purpose: one culled without the other would leave the canvas's layers
  unmatched. Gradients are Avalonia brushes in the object's own coordinates.
- **Object styles** (`Document.ObjectStyles`, Core's `ObjectStyles.cs`) copy their look onto objects
  (`ObjectStyle.ApplyTo`); `PageObject.ObjectStyle` only names the style. A new look property of objects belongs in
  `ObjectStyle`, its `From` and `ApplyTo`.
- **Text to outlines** (`Outlines.cs`) records `PageRenderer.DrawCharacters` into a `DrawingGroup` and turns each
  `GlyphRun`'s glyphs into Skia glyph paths, reading the font file the run was shaped with through
  `GlyphTypeface.PlatformTypeface.TryGetStream` (so the glyph numbers match). A path's parts start at a node with
  `Start`; `PathObject.Parts()` splits them, and anything walking `Nodes` as one run should use it.
- **PDF pages** are read by Core's `PdfReader` into the interpreter's own objects (`PsDict`, `PsArray`, `PsName`,
  `PsString`; a stream's bytes under `PdfReader.StreamKey`), deep-resolved so shadings, functions and colour spaces
  go straight to the PostScript code. `PdfContent.cs` is an `Interpreter` partial running content streams into
  `Ops`; PDF-only state (stroke colour, alpha, blend, soft mask, text) is `GState.Pdf`. Opacity, blending and soft
  masks are `PsGroup` ops, drawn by `EpsRender` as Skia layers. Damaged operators are passed over silently; set
  `AVAPS_PDF_DEBUG` to see them as notes. Don't make a pattern or mask inside an expression that holds `G` (they swap
  it). `PdfCompare` (a test that does nothing unless `AVAPAGESTREAM_PDFS` and `AVAPAGESTREAM_PDF_OUT` are set) draws
  PDFs beside Poppler's `pdftoppm` for checking. Printer-font outlines need Avalonia running: such tests are
  `[AvaloniaFact]`.
- **Protected PDFs**: `PdfSecurity` (Core's PostScript folder) and `PdfReader(bytes, password)`; a placed PDF's
  password is in `Document.Passwords` by image key, carried to a linked file's new version. The tests' `PdfCrypt`
  makes protected files (only with a cross-reference table, not `Build(xrefStream: true)`).
- **Every colour the document uses** is listed by `Colours.Used` (fills, strokes, gradients' stops, shadows, table
  headers, text, paragraph rules) and changed by `Editor.Recolour` for swatch renames and deletes. Anything new that
  holds a colour goes in both, or it misses CMYK conversion, spot inks and swatch renames.
- **Positions are points**, from the top-left of the page an object is on, rounded to 1/100 pt where the mouse sets them.
- **Subclassed controls need `StyleKeyOverride`** (as `SidePanel` has) or the theme gives them no template.
- **Draw a PDF page as one visual.** `DrawingContextHelper.RenderAsync` draws from the canvas's corner whatever
  transform the canvas has, so a booklet sheet is one `SheetVisual` placing its pages itself.
- **Small caps and scripts are ways of setting text.** Measure with `TextFlow.Measure` and draw with `TextFlow.AsSet`
  and `SmallCapsPieces`; never pass a format with `SmallCaps` or a `Script` straight to the measurer.
- **PDF metadata starts from `SKDocumentPdfMetadata.Default`**: a bare initialiser zeroes the encoding quality, which
  turns every picture into the worst JPEG. The export test checks for it.
- **Errors say what to do**, in the status bar, never as a bare exception.
- **Warnings are errors** (`TreatWarningsAsErrors`), and nullable reference types are on.
