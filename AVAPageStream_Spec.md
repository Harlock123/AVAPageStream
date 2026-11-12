# AVAPageStream: Specification

### A desktop publishing program for Windows, macOS and Linux, built with .NET 10 and Avalonia 12

AVAPageStream re-imagines PageStream, the frame-based desktop publishing program from Soft-Logik (later Grasshopper)
that many people first met on the Amiga. It keeps what made PageStream good: pages laid out as frames you draw on a
pasteboard, text that flows from frame to frame and column to column, master pages, rulers and guides, and output that
looks the same as the screen. It is a new program, written from scratch, with its own file format and its own look;
it is not affiliated with Soft-Logik or Grasshopper and reads no PageStream files.

---

## Contents

1. [Goals](#1-goals)
2. [Ideas the program is built on](#2-ideas-the-program-is-built-on)
3. [The window](#3-the-window)
4. [The document](#4-the-document)
5. [Objects](#5-objects)
6. [Text](#6-text)
7. [Tools and editing](#7-tools-and-editing)
8. [Pages and master pages](#8-pages-and-master-pages)
9. [Rulers, guides and snapping](#9-rulers-guides-and-snapping)
10. [Colour](#10-colour)
11. [Undo](#11-undo)
12. [Files](#12-files)
13. [Output](#13-output)
14. [Keyboard](#14-keyboard)
15. [Architecture](#15-architecture)
16. [Testing](#16-testing)
17. [Milestones](#17-milestones)

---

## 1. Goals

- **What you see is what prints.** The page on screen, a PNG export and a PDF are drawn by the same code from the
  same layout, so line breaks, positions and fonts match exactly.
- **Frames and flowing text.** Every piece of a page is an object in a frame. Text lives in *stories* that flow
  through a chain of linked text frames, and through each frame's columns, reflowing as you type, resize or relink.
- **Precise.** Positions and sizes are typed in the units you think in (points, picas, inches, millimetres or
  centimetres) and kept internally as points (1/72 inch), so nothing drifts when units change.
- **Self-contained documents.** A document file carries its pictures with it, so it opens the same on another machine.
- **Fonts that travel.** Liberation Serif and Liberation Sans ship inside the program, so a document that uses them
  looks the same everywhere; installed system fonts can be used too.
- **Testable.** Everything but the window lives in a Core library with no Avalonia in it, and the window is driven by
  headless tests, which also take the README's screenshots.

### Not goals (for now)

- Reading or writing PageStream, QuarkXPress, InDesign or Scribus files.
- Colour separations, trapping and imposition for commercial printers.
- Right-to-left and vertical scripts (text is laid out left to right).
- Collaborative editing.

---

## 2. Ideas the program is built on

| Idea | What it means here |
| --- | --- |
| **Pasteboard** | The grey area around the page. Objects can sit on it, half on and half off the page, while you work; only what is on the page is exported. |
| **Frame** | An object's rectangle on the page: its position (X, Y of the top-left corner from the page's top-left) and size. |
| **Story** | A body of styled text. It belongs to the document, not to a frame. |
| **Chain** | The ordered list of text frames a story flows through. Text fills the first frame's columns, then the next frame's, and so on. A story with more text than its chain can hold shows an *overflow* mark on its last frame. |
| **Master page** | A page of objects that appear behind every page that uses it: headers, footers, page numbers, column guides. |
| **Paragraph style** | A named set of character and paragraph settings. Changing the style changes every paragraph that uses it. |

---

## 3. The window

```
┌────────────────────────────────────────────────────────────────────────────────────┐
│ File  Edit  View  Object  Text  Page  Help                                         │ menu
├────────────────────────────────────────────────────────────────────────────────────┤
│ [New][Open][Save] │ [Undo][Redo] │ zoom 100% ▾ │ page ◀ 3 of 12 ▶ │ units: pt ▾      │ tool bar
├──┬──────────────────────────────────────────────────────────────┬──────────────────┤
│▲ │ ┌──────── ruler ───────────────────────────────────────────┐ │ Object │ Text │  │
│T │ │                                                           │ │ Pages │ Document │
│▭ │ │        ┌───────────────────────────┐   pasteboard         │ │                  │
│◯ │ │        │ page                      │                      │ │  X  [ 54 pt ]    │
│╱ │ │        │  ┌────────┐  ┌────────┐   │                      │ │  Y  [ 72 pt ]    │
│▣ │ │        │  │ text   │  │ text   │   │                      │ │  W  [240 pt ]    │
│⛓ │ │        │  │ frame  │→ │ frame  │   │                      │ │  H  [300 pt ]    │
│✋│ │        │  └────────┘  └────────┘   │                      │ │  Fill   [■ none] │
│  │ │        └───────────────────────────┘                      │ │  Stroke [■ black]│
│  │ └───────────────────────────────────────────────────────────┘ │                  │
├──┴──────────────────────────────────────────────────────────────┴──────────────────┤
│ Text frame 1 of story 1 · X 54 Y 72 W 240 H 300 pt │ cursor 120.5, 300.0 pt │ 100% │ status bar
└────────────────────────────────────────────────────────────────────────────────────┘
```

- **Toolbox** (left): Select, Text, Rectangle, Ellipse, Line, Picture, Link and Hand, each with a key (section 14).
- **Page view** (centre): the pasteboard with the current page on it, rulers along the top and left, scroll bars,
  and zoom from 10% to 1600%. Ctrl+wheel zooms about the mouse; the wheel scrolls; Shift+wheel scrolls sideways.
- **Side panel** (right), in tabs:
  - **Object**: position and size in the current units, fill and stroke colour, stroke width, corner radius
    (rectangles), lock; for text frames, columns, gutter and inset; for pictures, how the picture fits its frame.
  - **Text**: paragraph style, font, size, bold, italic, underline, colour, tracking, alignment, leading, indents and
    space before and after. It edits the selected text when editing, or all of the selected frames' text otherwise.
    Paragraph styles are made, renamed, redefined from a paragraph and deleted here.
  - **Pages**: the pages in order with their master, add (before or after), delete, move, and which master each uses;
    the master pages, to add, rename and edit.
  - **Document**: page size (Letter, Legal, Tabloid, A3, A4, A5, B5, custom), orientation, margins, columns and gutter
    for new pages, and the units.
- **Status bar**: what is selected and its frame, the cursor's position on the page in the current units, the zoom,
  and messages (what was saved, why something could not be done).

The window is built in code (no XAML), dark, like the other AVA applications; the page itself is always white paper.

---

## 4. The document

```
Document
├── Setup: page size, margins, columns, gutter, units
├── Styles: paragraph styles by name
├── Colours: named swatches
├── Stories: id → Story
├── Images: hash → bytes (PNG, JPEG, BMP, GIF, WebP)
├── Masters: list of MasterPage (name, guides, objects)
└── Pages: list of Page (master name, guides, objects)
```

- Every measure is a `double` in points. A page's origin is its top-left corner; Y grows downwards.
- Objects on a page are drawn in list order: the last is on top.
- Every object and story has an id unique in the document, so chains and selections refer to them by id.
- Page numbers count from 1. A page's *side* is left or right (odd pages right) for facing-page masters (milestone 2).

### Page sizes

| Name | Size (pt) | Size |
| --- | --- | --- |
| Letter | 612 × 792 | 8.5 × 11 in |
| Legal | 612 × 1008 | 8.5 × 14 in |
| Tabloid | 792 × 1224 | 11 × 17 in |
| A3 | 841.89 × 1190.55 | 297 × 420 mm |
| A4 | 595.28 × 841.89 | 210 × 297 mm |
| A5 | 419.53 × 595.28 | 148 × 210 mm |
| B5 | 498.9 × 708.66 | 176 × 250 mm |

Landscape swaps width and height.

### Units

| Unit | Points |
| --- | --- |
| point (pt) | 1 |
| pica (pc) | 12 |
| inch (in) | 72 |
| millimetre (mm) | 72 / 25.4 |
| centimetre (cm) | 720 / 25.4 |

A typed measure may carry its own unit (`3in`, `12p6` for 12 picas 6 points, `25 mm`), which overrides the current
unit; plain numbers are in the current unit. Simple sums (`72+18`, `2in-0.25in`) are allowed.

---

## 5. Objects

Every object has an id, a frame (X, Y, width, height), a rotation (degrees clockwise about the frame's centre), a
fill, a stroke (colour and width), a name, text wrap (on or off, a standoff, and by frame or by shape), and *locked*
(locked objects can be selected but not moved, resized or deleted). Every move and resize goes through the frame, so
a group moves and scales what it holds and a path its points.

| Kind | Extra settings | Notes |
| --- | --- | --- |
| **Rectangle** | corner radius | |
| **Ellipse** | | Fills its frame. |
| **Line** | which diagonal (down or up), arrowheads at either end | Drawn corner to corner across its frame; a frame may have zero width or height. |
| **Text frame** | story id, columns, gutter, inset | Shows its part of the story; empty frames show their columns as faint guides. |
| **Picture** | image hash, fit (stretch, fit, fill, actual size), crop offset | PNG, JPEG, GIF, BMP and WebP; SVG, drawn as vector through Svg.Skia (on screen and in PDFs); EPS, sized by its bounding box and shown by Ghostscript at 300 dpi when it is installed, else by its TIFF preview (in a DOS binary EPS's header) or its EPSI preview, else as a labelled box. A missing picture shows a crossed box. |
| **Group** | children | Moves, resizes (scaling what it holds), arranges, copies and deletes as one. Its frame is the box round what it holds. Double-click a text frame in a group to type in it. Groups don't rotate (what is in them can). |
| **Table** | columns and rows (widths and heights), a story for each cell, header rows and their fill, cell inset; rules in the stroke | Each cell's story is set in its cell, less the inset; rows keep their height, a cell whose text doesn't fit shows a red mark, and Fit rows to text makes each row as tall as its tallest cell needs. Resizing scales the columns and rows. Tables don't rotate. |
| **Polygon / Bézier path** | points, each with optional handles in and out; closed or open | Its frame is the box round its points and handles. Only a closed path is filled. |

Fills and strokes are a colour or *none*. A stroke is centred on the frame's edge.

---

## 6. Text

### The story

A story is its text (paragraphs end with a new-line character) plus:

- **Character runs**: consecutive stretches of the text, each with one complete `CharFormat`: font family, size,
  bold, italic, underline, colour, tracking (in thousandths of an em) and baseline shift.
- **Paragraph formats**: one `ParaFormat` per paragraph: the paragraph style's name, alignment (left, centre, right,
  justified), leading (0 = automatic, 120% of the largest size on the line), left, right and first-line indents, and
  space before and after.

Runs never have zero length, and two neighbouring runs never have the same format: every edit puts the story back in
that form, so equal stories compare equal.

Applying a paragraph style sets the paragraph's format and all of its characters to the style's. Redefining a style
updates every paragraph that uses it, keeping bold and italic set locally (milestone 2: keep every local change).

### Flowing

Layout takes a story and the list of *areas* it may fill (each frame in the chain, inset, split into its columns,
in order) and produces *lines*, each in one area at a baseline, made of *pieces* of text in one format at an X.

1. Paragraphs are broken into words at spaces and after hyphens; spaces stay with the word before them.
2. Words fill a line until the next would not fit; a word longer than a whole line is broken between characters.
3. Each line's height comes from the largest font on it: ascent and descent from the font, leading from the paragraph.
   A line that does not fit in the rest of an area moves to the top of the next area. The first line of an area sits
   with its ascent at the area's top.
4. Space before is skipped at the top of an area. Indents narrow the line.
5. Alignment moves the line; justified lines (except the last of a paragraph) spread their spaces.
6. Text left when the areas run out is *overset*: the last frame shows a red ⊞ mark, and the status bar says how much.

Measuring is behind an interface (`ITextMeasurer`), so Core lays out text without Avalonia and tests use a measurer
with fixed widths; the program measures with the real fonts.

Layout is redone when the story, the chain, a frame's size or columns change, and never for a frame the change
cannot reach.

### Editing text

Choosing a text frame with the Text tool (or double-clicking it with Select) puts the caret in the story. The caret
moves through the whole story, across frames.

- Typing inserts in the format of the character before the caret (or after, at a paragraph's start).
- Backspace, Delete, Enter (new paragraph, same format), arrows, Home, End, Ctrl+arrows by word, Ctrl+Home/End to the
  story's ends, Page Up/Down by frame.
- Shift extends the selection; dragging selects; double-click selects a word, triple-click a paragraph; Ctrl+A
  selects the whole story.
- Ctrl+B, Ctrl+I and Ctrl+U toggle bold, italic and underline on the selection.
- Cut, copy and paste (plain text; milestone 2: styled within the program).
- Escape leaves the text and selects its frame.

### Text wrap (milestone 2)

- Any object can be set to wrap (Object tab › Text wrap), with a standoff (6 pt to start with). Text keeps the
  standoff away from the object's frame.
- Wrap affects the text under it. A frame keeps out of the wrapping objects drawn above it on its page, and on a page
  out of every wrapping object on its master (which is drawn under the page). So a pull quote's frame on top of its own
  wrapping panel keeps its text, while the columns underneath flow round both.
- A line beside a wrapped object is set in each stretch of room either side of it, in order. A word that fits whole
  in none of them waits for the line below, and where nothing fits at all the text moves on to below the object (or to
  the next frame). Up and Down move between heights, keeping to the caret's column.

### Fields (milestone 2)

- Text › Insert page number (Ctrl+Shift+3) and Insert page count put a field in the story: a single character that
  shows the number of the page it is on, or how many pages there are.
- On a master page a page number shows "#", and under each page it shows that page's number: a master's stories are
  set again for each page they are drawn under.
- Elsewhere a story's fields count from the page its first frame is on.

### Find and replace (milestone 2)

- Edit › Find and replace (Ctrl+F) opens a bar under the page: find, replace with, Match case and Whole word. Enter
  or F3 finds the next match (Shift: the one before), going through the stories in reading order (page by page, then
  the master pages) and round to the start. Each match is shown selected, on its page.
- Replace replaces the match shown and finds the next. Replace all replaces every match in one step to undo, each in
  the format of the text it replaces.

### Placing text (milestone 2)

- Object › Place text… reads a .txt or .md file into the story at the caret, or onto the end of the selected text
  frame's story, as one step. Plain text becomes a paragraph a line.
- Markdown: # headings take Heading and ## and smaller take Subheading. Paragraphs take Body. List items get a bullet
  or a number and a hanging indent. Quotes are indented and in italic. **Bold** and *italic* carry over. `Code` and
  [links](…) keep their text; pictures are left out.

### Type (milestone 2)

- **Tabs**: each paragraph has tab stops (Text tab › Tabs, typed as "1in, 4in right ., 2.5in decimal"): left,
  right, centre and decimal, each with an optional leader character drawn across the gap. Past the last stop, tabs
  fall every half inch; a hanging indent's left indent is a stop too, so a bullet and a tab line list text up.
  Lines with tabs aren't justified.
- **Drop caps**: Text tab › Drop cap, so many lines deep and so many letters wide. The capital stands from the last
  line's baseline up to the top of the first line's capitals; the lines beside it are indented by its width.
- **Hyphenation**: per paragraph (Text tab › Hyphenate), by Liang's algorithm with the American English patterns
  and exceptions of the hyph-utf8 package (bundled in Core/Hyphenation with their copyright notice). At least two
  letters stay before a break and three after it. A hyphen is shown, not put in the text.
- **Kerning**: a pair split only by colour or underline keeps the font's kerning; Alt+Left and Alt+Right in the text
  tighten and loosen the space after the character before the caret (or after each selected character) by 10
  thousandths of an em, 50 with Shift. It is tracking on those characters.
- **Wrap by shape**: an ellipse or a closed path set to follow its shape keeps text out of its outline (pushed out by
  the standoff) rather than its frame; a turned object wraps by its turned frame.

### The story editor (milestone 2)

Text › Story editor (Ctrl+Shift+E) shows the story of the frame selected (or being typed in) as plain text in a
window of its own, with its word, paragraph and frame counts and how much is overset. Typing there replaces only the
stretch that changed, so the formats round it stay; changes on the page show there at once; the paragraph style of
the caret's paragraph is shown and can be set. Deleting the story's last frame closes it.

### Styled copy and paste (milestone 2)

Text copied while typing is kept with its formats as well as put on the clipboard as plain text. Pasted back here
(while the clipboard still holds that text), it keeps its formats; the paragraph it lands in keeps its own format,
and each new paragraph it makes takes the one it had. Text from other programs is pasted plain.

### Small caps, super- and subscript (milestone 3)

Text tab › Sc, x² and x₂. Small caps set lower-case letters as capitals at 72% of the size; superscript and
subscript set the text at 60% of the size, raised by a third of the size or lowered by a sixth. They are ways of
setting the characters, not changes to them: the text stays as typed, and searching finds it as typed.

### Table of contents (milestone 3)

Text › Table of contents… asks which paragraph styles to list (the first ticked is the top level) and puts the
contents at the caret, or at the end of the selected frame's story: a paragraph for each paragraph in those styles on
the pages (not the masters), in page order, as "text, tab, page number" (– for one that is overset). Each level gets
a style of its own, Contents 1, Contents 2…, indented a step a level, with a right tab and dot leader at the column's
width; make the contents again to bring the page numbers up to date (the styles are remade).

### Spelling (milestone 3)

- The word lists are SCOWL's (size 60, the lists Hunspell's English dictionaries are made from, every inflected form
  included), American or British English (Document tab › Spelling), bundled with SCOWL's copyright notice.
- A word is right if the list has it, or has it in lower case and it is capitalised or in capitals; curly apostrophes
  count as straight ones, each part of a hyphenated word is checked, and words with digits pass.
- Misspelt words get a red wavy line on screen (View › Check spelling as you type), never on paper.
- Text › Check spelling (F7) goes from the caret through the stories in reading order and round, showing each
  misspelt word selected on its page, with suggestions (words one or two changes away, commonest first, swapped
  letters first among them): Change, Change all, Ignore, Ignore all (kept in the document), and Add to dictionary
  (the user's own words.txt, in the settings folder).

---

## 7. Tools and editing

| Tool | Key | What it does |
| --- | --- | --- |
| Select | V | Click to select (Shift adds or removes), drag on the page for a marquee, drag an object to move it, drag one of eight handles to resize (Shift keeps the shape), double-click a text frame to edit its text. Arrow keys nudge by 1 pt (Shift: 10 pt). |
| Text | T | Drag to draw a text frame and start typing in it; click in a text frame to put the caret there. |
| Rectangle | R | Drag to draw (Shift: square). |
| Ellipse | E | Drag to draw (Shift: circle). |
| Line | L | Drag to draw (Shift: at 45° steps). |
| Picture | P | Drag a frame, then pick an image file for it; clicking an existing picture replaces its image. |
| Link | K | Click a text frame, then another: the second joins the first's chain just after it. Clicking an empty frame with the story's last frame picked adds it to the end. Alt+click a frame takes it out of its chain. |
| Hand | H (or hold Space) | Drag to scroll. |
| Table | G | Drag a box for a table of 4 rows and 3 columns, its first row a header; the caret goes into its first cell. Double-click a cell (or click it with Text) to type in it. Object › Table puts rows and columns in, and takes them out, where the caret is. |
| Pen | B | Click for corner points, drag for smooth Bézier points (the handle where the mouse lets go), Shift for 45° steps. Click the first point again to close the shape; double-click or Enter ends it open; Escape gives it up. Double-click a path with Select to drag its points and handles; Delete takes out the last point dragged. |

- A new object is drawn with the last fill and stroke used for its kind.
- **Object menu**: bring to front, bring forward, send backward, send to back; group (Ctrl+G) and ungroup
  (Ctrl+Shift+G); lock and unlock; delete; duplicate (Ctrl+D, offset 12 pt); align left, centre, right, top, middle,
  bottom, and space across or down (Object tab), by the box each object covers once turned.
- **Rotation**: a round handle above a selected object's top turns it about its centre (Shift: 15° steps), or type
  the angle in the Object tab. Turned objects are hit, resized (in their own frame, the opposite edge staying put),
  outlined and wrapped where they really are; a turned text frame is typed into and clicked in its own frame.
- Text frames show their chain: with a linked frame selected, arrows from the bottom-right of each frame to the
  top-left of the next show the flow.
- Clicking a text frame's overset mark with Select starts the Link tool from that frame.

---

## 8. Pages and master pages

- A new document has one page using the master "A-Master", sized from the setup (Letter, ½ in margins, one column).
- Pages can be added before or after the current one, deleted (never the last), and moved.
- Each page uses one master, or none. A master's objects are drawn under the page's objects and cannot be selected on
  the page; edit them by opening the master in the Pages tab, where the page view shows the master itself.
- Margins and columns are shown as guides (magenta and violet): the document's, or a master's own (Pages tab ›
  This master's guides), for every page that uses it.
- Text frames on a master that hold a page-number field show each page's own number.
- **Facing pages** (Document tab): odd pages are right-hand pages and even pages left-hand, page 1 alone on the
  right. The left and right margins become inside and outside, mirrored on left pages. The view shows a spread, both
  pages side by side with the spine between, and clicking the other page works on it. Each master has a left side,
  drawn under left pages, shown beside its right side when the master is edited.

---

## 9. Rulers, guides and snapping

- Rulers along the top and left measure from the page's top-left in the current units, with ticks that thin out as
  the zoom shrinks, and a marker following the mouse.
- Dragging from a ruler onto the page makes a guide (horizontal from the top ruler, vertical from the left); dragging a
  guide moves it; dragging it back onto a ruler removes it. Guides belong to the page.
- **Snap** (View › Snap to guides, on by default): moving, resizing and drawing snap to margins, column edges, page
  edges, guides and the page's centre lines within 5 screen pixels.
- **Grid** (milestone 2): View › Show grid draws the document grid (18 pt to start with; Document tab › Grid), and
  View › Snap to grid snaps to its lines as well as, or instead of, the guides.

---

## 10. Colour

- Colours are sRGB with alpha, stored as `#RRGGBB` or `#AARRGGBB`, or as `@Name` for a swatch (`@Name 40%` for a
  tint of it, lightened toward white).
- The document keeps named swatches: Black, White, Grey, Light grey, Red, Green, Blue, Cyan, Magenta, Yellow and any
  you add (Document tab › Swatches). A colour picked by a swatch's name (typed, or with a swatch button) refers to
  the swatch, so changing the swatch's colour changes every fill, stroke, piece of text and style using it. Renaming
  a swatch renames its references; deleting one leaves what used it with its colour as a plain value.
- **CMYK and spot colours** (milestone 3): a swatch can be made of CMYK inks (typed as "0 100 100 0", "C0 M100 Y100
  K0" or "cmyk 0,100,100,0") and marked as a spot ink. Its screen colour comes from the plain conversion (each of
  red, green and blue is what its opposite ink and the black leave of white; no colour profile). The Document tab
  lists the inks a document prints with: process, and each spot ink used. PDFs are written in RGB, as SkiaSharp
  writes no CMYK, so a printer working from them gets the converted colours.

---

## 11. Undo

Every change is one step that Undo (Ctrl+Z) takes back and Redo (Ctrl+Y or Ctrl+Shift+Z) puts back, without limit
until the document is closed. Typing a run of characters, or nudging repeatedly, is one step. A step is the document
as it was (the model is small: images are kept outside the steps, shared by every state), so undo can never get a
change partly wrong. The title shows `•` while there are changes not saved.

---

## 12. Files

- **Templates** (milestone 3): File › Save as template writes the document as a `.avpt` (the same format) in the
  templates folder in the user's settings folder; File › New from template starts an untitled document from one of
  those, from any `.avpt` file, or from the built-in Newsletter (Letter, a masthead and two linked columns, page
  numbers in the footer) and Booklet (eight A5 facing pages with one story through them, page numbers outside).
- **`.avps`**: a ZIP archive holding `document.json` (everything but the images, as indented JSON, with a format
  version) and `images/<sha256>.<ext>` for each picture used. Images no object uses are left out on saving.
- Saving writes a new file beside the old one and then replaces it, so a failed save never damages the old file.
- A newer format version than the program knows is refused with a message saying so.
- **Open recent** keeps the last ten files. Closing with unsaved changes asks whether to save.
- Settings (units, recent files, snap) are kept in the user's settings folder as `settings.json`.

---

## 13. Output

- **Export PDF**: every page (or a range) as vector PDF with its fonts embedded, at the page's size.
- **Export PNG**: the current page or every page at a chosen resolution (72 to 600 dpi), one file per page.
- Both draw with the same renderer as the screen, with guides, frame outlines and the pasteboard left out.
- **Print** (File › Print, Ctrl+P; milestone 3): the same choice of pages as exporting, then the pages are written
  to a PDF and sent to the system's printing: `lp` (CUPS) on Linux and macOS, the PDF's Print action on Windows.
  Avalonia has no printing of its own; where there is no `lp`, the window says where the PDF is.
- **Booklets** (milestone 3): exporting or printing can arrange the pages as a saddle-stitched booklet: each sheet
  side twice a page wide, with two pages on it in the order that folds and nests into a booklet (8|1, 2|7, 6|3, 4|5
  for eight pages), made up to a multiple of four with blank pages. Print both sides, flipping on the short edge.

---

## 14. Keyboard

| Keys | Does |
| --- | --- |
| Ctrl+N, Ctrl+O, Ctrl+S, Ctrl+Shift+S | New, open, save, save as |
| Ctrl+E | Export PDF |
| Ctrl+P | Print |
| Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z | Undo, redo |
| Ctrl+X, Ctrl+C, Ctrl+V | Cut, copy, paste (objects, or text when editing) |
| Ctrl+D | Duplicate |
| Delete, Backspace | Delete the selected objects (when not editing text) |
| Ctrl+A | Select every object on the page (or all the story's text when editing) |
| Ctrl+0, Ctrl+1, Ctrl+=, Ctrl+- | Fit page, actual size, zoom in, zoom out |
| Page Up / Page Down | Previous, next page (when not editing text) |
| Ctrl+] / Ctrl+[ , Ctrl+Shift+] / Ctrl+Shift+[ | Bring forward, send backward; to front, to back |
| V T R E L P K H B G | Tools (when not editing text) |
| Tab, Shift+Tab | In a table: the next cell (a new row after the last), the one before |
| F7 | Check spelling |
| Ctrl+G, Ctrl+Shift+G | Group, ungroup |
| Ctrl+F, F3, Shift+F3 | Find and replace; find the next, the one before |
| Ctrl+Shift+3 | Insert a page number (when editing text) |
| Ctrl+Shift+E | Story editor |
| Alt+Left, Alt+Right | Kern tighter, looser (Shift: more) |
| Escape | Stop editing text; then clear the selection; cancels a drag |

---

## 15. Architecture

```
AVAPageStream.slnx
├── src/AVAPageStream.Core/     no Avalonia: the model, layout, editing, undo, files
│   ├── Units.cs                Units, measure parsing and formatting
│   ├── Colours.cs, Box.cs      colour parsing and swatches; points and rectangles
│   ├── Model.cs                Setup, Page, MasterPage, SheetRef, Guide, PageObject and its kinds
│   ├── Document.cs             Document, and the ImageStore its undo states share
│   ├── Story.cs                Story, CharFormat, ParaFormat, ParagraphStyle; inserting, deleting, formatting
│   ├── TextFlow.cs             ITextMeasurer, Area, Line, Piece, TextLayout: a story set through its areas
│   ├── Chains.cs               a story's frames and areas, linking and unlinking
│   ├── Editor.cs               the document being edited: every change, and undo and redo
│   ├── TextNav.cs              moving by words; picking a word
│   ├── DocumentFile.cs         reading and writing .avps
│   ├── Snapping.cs             snap targets and snapping
│   └── AppSettings.cs
├── src/AVAPageStream/          the Avalonia program
│   ├── App.cs, Program.cs
│   ├── Look.cs                 colours of the window
│   ├── FontSupport.cs          the bundled fonts, and the measurer that uses real fonts
│   ├── PageRenderer.cs         draws a page (and its master) to a DrawingContext: used by the screen and exports
│   ├── PageView.cs             the pasteboard, rulers, tools, selection, text editing
│   ├── Panels.cs               the side panel tabs
│   ├── MainWindow.cs           menus, tool bar, status bar; its controls are public for tests
│   ├── Export.cs               PDF (through SkiaSharp's PDF document) and PNG
│   └── Dialogs.cs
└── tests/AVAPageStream.Tests/  xUnit v3 + Avalonia.Headless.XUnit
```

- **Rules**: nothing in Core touches a window; every change to the document goes through `Editor` methods that record
  one undo step; layout is a pure function of the story, the areas and the measurer, cached by the window per story;
  errors are shown in the status bar in plain words, never as a bare exception.
- **Dependencies**: Avalonia 12.1.4 (Desktop, Fluent theme, Inter font), SkiaSharp (already under Avalonia, for PDF),
  System.Text.Json and System.IO.Compression from .NET.

---

## 16. Testing

- **Core**: units and measure parsing; colour parsing; story editing keeps its runs normal; formats apply to the right
  characters; layout breaks lines, fills columns and frames in order, aligns and justifies, and reports overset text
  (with a fixed-width measurer); chains link and unlink; undo and redo; files save and load with their pictures and
  refuse newer versions; snapping.
- **Window** (headless, real fonts): drawing each kind of object with the mouse; moving, resizing and nudging; typing
  into a frame and the text flowing on into a linked frame; styling text from the Text tab; pages added, moved and
  deleted; master objects showing on pages; undo; saving and opening; exporting a PDF and PNGs.
- **Screenshots**: with `AVAPAGESTREAM_SHOTS` set to a folder, the window tests save pictures of the window there; the
  README's screenshots come from them.

---

## 17. Milestones

### Milestone 1: a working page layout program

Document and pages with a master page; rectangles, ellipses, lines, pictures and text frames; flowing linked text
through columns and frames with character and paragraph formats and paragraph styles; in-place text editing; select,
move, resize, nudge, arrange, duplicate, delete; rulers, guides and snapping; zoom and scrolling; the four side panel
tabs; undo and redo; `.avps` files; PDF and PNG export; tests and CI publishing for eight platforms.

**Status (11 November 2026):** done. Everything above is built and tested, along with scroll bars, Page Up/Down by
frame while editing, the export dialog (a page range for PDF and PNG; the PNG resolution), dragging pages in the
Pages tab, renaming styles, cropping pictures by offset (Alt+drag or the Object tab), and layout redone only for the
stories a change reaches.

### Milestone 2: a publishing program

Facing pages and spreads; per-master margins and columns; page number fields; groups; align and distribute; polygons
and Bézier paths; rotation; text wrap; tabs; drop caps; hyphenation; kerning; named swatches that update their users;
the story editor; find and replace; importing text and Markdown; styled copy and paste; a document grid.

**Status (11 November 2026):** done. Everything above is built and tested, with line arrowheads.

### Milestone 3: print

Printing; CMYK and spot colours; SVG and EPS pictures; spelling; small caps, super- and subscript; tables; a table of
contents from paragraph styles; templates; booklet printing.

**Status (11 November 2026):** done. Everything above is built and tested. Two parts go as far as the libraries do:
PDFs are RGB (CMYK colours are converted), and EPS needs Ghostscript to be shown in full.

### Milestone 4: a long-document program

What books, magazines and reports need beyond milestone 3:

- **Character styles**: named sets of character settings (Emphasis, Strong, Small caps…) applied to stretches of
  text over their paragraph's style, which update their text when redefined.
- **Keeping text together**: keep a paragraph with the next (a heading with its first paragraph), keep a paragraph's
  lines together, and widow and orphan control (at least so many lines at the top and the foot of a column).
- **Page-number sections**: a page can start a section, its numbers starting again from any number, as 1, 2, 3, as
  i, ii, iii or I, II, III, or as a, b, c or A, B, C, with a prefix ("A-1").
- **Layers**: the document's objects on named layers, drawn in the layers' order, each layer shown or hidden, locked
  or not, printed or not.
- **Tables that run on** across linked table frames, their header rows repeated.
- **A baseline grid** for text to sit on across columns.
- **Bleed and crop marks** in PDFs for printing to the edge.
- **Linked pictures** kept as files beside the document, updated when the file changes.
- **Autosave and recovery** after a crash.
- **Text on a path.**
- **Footnotes**, numbered and set at the foot of the column.

**Status (12 November 2026):** done. Limits: PDFs carry no separate trim and bleed boxes (SkiaSharp can't write
them), so a print shop works from the crop marks; find and spelling don't look in footnotes or text on paths; and
text on a path is edited in the story editor, not on the page.

### Milestone 5: ready to go out

Making what is there dependable on its way to a printer or a reader, before adding more:

- **Everywhere text is**: find and replace and the spelling check go through footnotes and text on paths too, and
  both are edited on the page like any other text: click into a footnote at the foot of its column, or into the
  letters along a path.
- **Preflight**: before exporting or printing, and from File › Preflight, a list of what would go wrong: overset
  text, table rows with no frame to go in, linked pictures that are missing or have changed, pictures with too few
  pixels for the size they are printed at (under 150 dots an inch by default), artwork that stops short of the bleed
  at a page's edge, and fonts that are neither bundled nor embeddable. Each entry shows the page and goes to the
  problem when picked.
- **Package**: File › Package… copies the document, its linked pictures and the font files it uses into one folder
  (links pointing at the copies), with a short report, ready to hand to a printer.
- **A better PDF**: bookmarks made from the headings (the paragraph styles a table of contents would use), entries
  of a table of contents that go to their pages, web addresses that open, and the document's title and author.
  Where SkiaSharp can't write something (trim and bleed boxes), see whether a pass over the finished file can add it.

**Status (12 November 2026):** done. The trim and bleed boxes and the outline of bookmarks are added by an update
appended to the PDF SkiaSharp writes (`PdfExtras.Finish`); links and destinations are SkiaSharp's own annotations.
Preflight asks before exporting and printing when it finds problems that will show (a setting turns that off). PDFs
can be written in CMYK, by rewriting the colours of the finished file (`PdfColour`): JPEG pictures stay RGB, and it
is not PDF/X (that needs an output intent and every picture converted). A font's licence is read
from its OS/2 table; one installed but not found by name in the usual font folders can't be packaged.

### Milestone 6: EPS without Ghostscript

EPS pictures drawn by AVAPageStream itself: a PostScript interpreter for what EPS files use, in C# in Core, drawing
into a Skia picture as SVG is drawn, so EPS artwork stays vector on screen and in PDFs (better than Ghostscript's
300 dpi bitmap), the same on every computer, with nothing to install. Ghostscript, when it is there, and then the
previews (TIFF, EPSI) are kept as fallbacks for what the interpreter doesn't do, and the picture's description says
which drew it. In stages, each usable on its own:

1. **The language and paths.** The PostScript language: the operand, dictionary, execution and graphics-state
   stacks; numbers, names, strings, arrays, procedures and dictionaries; the operators for arithmetic, stack and
   control (if, ifelse, for, repeat, loop, forall, exit, stopped), dictionaries (def, begin, end, load, where,
   known) and strings and arrays; save and restore. Paths (moveto, lineto, curveto, arc, arcn, arct, closepath, the
   relative forms, rectfill, rectstroke), fill and eofill, stroke with width, cap, join, miter limit and dashes,
   clip and eoclip, the current transformation matrix (translate, scale, rotate, concat), gsave and grestore, and
   colour in grey, RGB, CMYK and HSB, with the Level 2 colour spaces DeviceGray, DeviceRGB, DeviceCMYK and Indexed.
   The EPS rules: showpage and device operators do nothing, and the picture is clipped to its bounding box. This
   covers most technical EPS: matplotlib, gnuplot, R, Inkscape and Cairo, LaTeX figures.
2. **Images.** image, colorimage and imagemask, with data read from the file (currentfile) through the filters EPS
   uses: ASCIIHexDecode, ASCII85Decode, RunLengthDecode, LZWDecode, FlateDecode and DCTDecode (JPEG, through
   SkiaSharp), and procedures that supply the data a piece at a time.
3. **Text.** findfont, scalefont, makefont, setfont, show and its relatives (ashow, widthshow, kshow, xshow, yshow,
   glyphshow), stringwidth and charpath. Fonts embedded in the file (Type 1, decrypted and their charstrings run;
   Type 3, whose procedures are PostScript; Type 42, TrueType inside PostScript) are drawn as outlines. The standard
   printer fonts map to the bundled Liberation fonts, made to the same widths: Times to Liberation Serif, Helvetica
   to Liberation Sans, Courier to Liberation Mono (to be bundled), and Symbol to a font with its characters.
4. **Illustrator, CorelDRAW and the rest.** Their EPS files carry large procedure sets (Adobe Illustrator's AI
   procsets, CorelDRAW's, QuarkXPress's, FreeHand's): a collection of real EPS files from each, run until they draw,
   adding what they need.

Not planned: Level 3 smooth shading of the mesh kinds (types 4 to 7: the simpler axial and radial ones, 2 and 3, are
in stage 1 if time allows), patterns, separations and spot colour spaces (DeviceN, Separation: drawn by their
alternative colour), and PostScript beyond one page. A file that uses them falls back to Ghostscript or its preview.

Tests: a folder of EPS files from each producer, each drawn and compared with Ghostscript's rendering of it (made once
on a computer that has Ghostscript, and kept with the tests), the drawings matching to within a few per cent of the
pixels; and the language tested operator by operator.

**Status (12 November 2026):** stages 1 and 2 done: the language, paths, colour, images (with every filter EPS
text uses, and JPEG through SkiaSharp) and axial and radial shading, both through shfill and shading patterns. Tested
operator by operator, and on EPS files from Cairo (rsvg-convert), pdftocairo, Poppler's pdftops and ImageMagick,
each compared with the picture rsvg-convert or ImageMagick draws of the same thing. Binary data straight in the
program (an 8-bit Flate or JPEG stream not wrapped in ASCII85 or hex) is not read yet.

Stage 3 done too: findfont, definefont, scalefont, makefont, selectfont and the show family (ashow, widthshow,
awidthshow, kshow, xshow, yshow, xyshow, glyphshow), stringwidth and charpath; Type 42 fonts through a TrueType reader
of our own (simple and composite glyphs), Type 1 fonts (eexec in hex or binary, charstrings with subroutines, flex,
hint replacement and seac), Type 3 fonts (BuildGlyph and BuildChar), and the printer fonts drawn with the bundled
Liberation fonts (Liberation Mono now bundled for Courier). Tested with a Type 1 font built and encrypted in the
tests, a Type 3 font like matplotlib's, and EPS text from pdftocairo and pdftops compared with rsvg-convert's
picture. Not done: the Symbol and Dingbats fonts' encodings, composite (Type 0 and CID) and CFF fonts, and
encoded number strings for xshow.

Since then, the gaps filled: binary data straight in the program (Flate by an inflater of our own that stops at its
end), strokepath, cshow, encoded number strings, the CIE colour spaces, masked images (ImageType 3 and 4), tiling
patterns, smooth shading of types 1 and 4 to 7, the Symbol and Dingbats fonts, composite fonts (FMapType 2, 4, 5, 6
and 9), CMaps, CIDFontType 0 and 2, and CFF font sets with Type 2 charstrings. Still not done: the escape-mapped
composite fonts (FMapType 3, 7 and 8), CIDFontType 1, and binary tokens. Next: stage 4, the big programs' procedure
sets, which needs EPS files from Illustrator, CorelDRAW, QuarkXPress and FreeHand to work through.

### Milestone 7: design

What a designer laying out a page reaches for, on the program's own shapes, frames and text:

- **Gradients**: linear and radial fills of any number of colour stops (swatches and tints among them) on shapes,
  frames and table cells, set and dragged in the Object tab and on the page; drawn the same on screen and in PDFs.
- **Transparency**: an opacity for each object (and its fill and stroke apart), and blend modes (multiply, screen,
  overlay and the rest), with text and pictures under them showing through.
- **Drop shadows and glows**: on any object or text frame: colour, opacity, offset and softness.
- **Rounded and cut corners** on rectangles and frames, each corner its own.
- **Paragraph rules**: a line above and below a paragraph, its weight, colour, length (the text, the column, or a
  measure) and offset, as part of the paragraph's style.
- **Object styles**: named sets of fill, stroke, corners, shadow, opacity and text wrap, applied to objects and
  updating them when redefined, like paragraph styles.
- **Text to outlines**: a frame's text (or a selection of it) turned into paths that can be reshaped and filled.
- **Placing PDF pages as pictures**: a page of a PDF placed like an SVG, kept as vector in the exported PDF.
  (Planned through PDFium; done instead with AVAPageStream's own reader, on the EPS interpreter's graphics and fonts,
  as PDFium would only give a bitmap and needs a native library for each platform.)

**Status (12 November 2026):** done. Gradients, transparency and blend modes, drop shadows and glows,
rounded and cut corners, paragraph rules (column, text or set length; they take no room, so space before and
after makes room for them), and object styles (an object keeps its style's name when changed by hand, shown as
changed; redefining puts every object in the style back to its look), and text to outlines (paths can now have
several parts and fill by the non-zero rule; a selection's letters stay in the text, clear), and placed PDF pages.
Gradients are set in the Object tab, or dragged on the page with the Gradient tool (D); an object with opacity, a
blend mode or a shadow is drawn as a Skia layer of its own, opened and closed round Avalonia's own drawing of it, so
its text and pictures are in it too.

The PDF reader (`PdfReader`, `PdfContent`, `PdfFonts` in Core's PostScript folder) reads cross-reference tables and
streams, object streams, and damaged files (by finding their objects); it draws a page's content, forms,
annotations' appearances, optional content, pictures (Flate, LZW, JPEG and the rest, with soft and stencil masks),
shadings and patterns, transparency groups, blend modes and soft masks (with transfer functions), and text in
embedded Type 1, CFF, TrueType, OpenType, Type 3 and composite fonts, or the Liberation fonts for those only named.
Compared against Poppler's drawing of PDFs from Skia, cairo, ReportLab, Ghostscript, LibreOffice, OpenOffice,
pdf-lib and TeX, the pages match but for smoothing. Not done: encryption (even with an empty password), JPEG 2000,
JBIG2 and CCITT pictures, predefined CMaps other than Identity (non-embedded CJK text), knockout groups, and Type 3
glyphs as a clip.

### Milestone 8: PDF in full

What it takes for any PDF to be placed, and for the PDFs AVAPageStream makes to go straight to a print shop. In
stages, each usable on its own:

1. **Protected PDFs.** The standard security handler, every revision: RC4 of 40 to 128 bits (revisions 2 to 4),
   AES-128 (4) and AES-256 (5 and 6), with crypt filters and unencrypted metadata. Most protected PDFs open with an
   empty password (they only restrict copying or editing): those are placed as any other. One that needs a password
   asks for it when placed, and keeps it with the picture so the page can be drawn again when the document opens.
2. **Scanned pictures.** Decoders of AVAPageStream's own for the three formats scanners and archives write: CCITT
   fax (Group 3 and 4), JBIG2 (generic, refinement, text and halftone regions, with global segments) and JPEG 2000
   (codestreams and JP2 files: tiles, both wavelets, colour transforms, any number of components and bit depths).
3. **Asian text in PDFs.** Adobe's predefined CMaps (BSD-licensed, bundled and compressed), so text in fonts a PDF
   only names is set from its CIDs and drawn with a CJK font installed on the computer (Noto CJK, Source Han,
   MS Gothic and the like), found as other fonts are.
4. **PDF/X-4 export.** An output intent with an ICC profile AVAPageStream writes for its own CMYK conversion (so the
   file says exactly how its colours were made), every colour and picture (JPEGs too) in CMYK or a spot ink, the
   trim and bleed boxes, fonts embedded, XMP metadata naming the standard; and preflight's checks for what PDF/X
   doesn't allow. With it, the reader's last gaps: knockout groups and Type 3 glyphs as a clip.

**Status (12 November 2026):** done. Stage 1: `PdfSecurity` opens files of every revision with the empty
password, the user's or the owner's (AES-256 by revision 6's hash), and `PdfReader` decrypts objects as it loads
them (not the protection's dictionary, cross-reference streams, objects inside object streams, or metadata left
plain). Passwords are kept in `Document.Passwords`, by image. The tests protect PDFs with an encryptor of their
own (`PdfCrypt`) and check each file against Poppler as well, when it is installed.

Stage 2 done: CCITT fax (`Ccitt.cs`, checked bit for bit against ImageMagick's coding of a known picture); JBIG2
(`Jbig2.cs` on `MqDecoder.cs`: generic regions in every template with prediction, MMR, refinement, symbol
dictionaries with refinement and aggregation, text regions, patterns and halftones; tested with streams from an
encoder in the tests, against Poppler and, for refinement, pdf.js; Huffman-coded symbols and text not read); and
JPEG 2000 (`Jpx.cs`: JP2 boxes, tiles, every progression and POC, precincts, PPM/PPT, SOP/EPH, every code-block
style, quantisation, ROI, both wavelets and colour transforms; OpenJPEG's samples decode exactly when lossless, and
within its single-precision rounding when lossy). Poppler, found wanting twice: it ORs the last byte of a replaced
JBIG2 region, and garbles a four-component JP2 with SMaskInData.

Stage 3 done: Adobe's CMaps (180, bundled by `tools/make_cmaps.py` as 1.7 MB) and each collection's CIDs to Unicode;
vertical writing by W2 and DW2.

Stage 4 done: PDF/X-4 (`PdfX.cs`, `IccCmyk.cs`): an output intent with a profile of the plain CMYK conversion
(checked with LittleCMS: it reads it as a CMYK output profile and converts as AVAPageStream does), XMP and Info that
agree, an /ID, PDF 1.6, trim boxes, transparency groups in CMYK, JPEGs converted, links left out. Knockout groups and
Type 3 glyphs as a clip are drawn. (With it, a bug in CMYK export fixed: SkiaSharp's small uncompressed forms were
written back deflated without saying so.) Not validated by a PDF/X checker, none being free to run here.

### Milestone 9: long documents, joined up

What a book, a manual or a report needs beyond flowing text: things that travel with the words, and parts that
are made from the rest of the document and stay right as it changes. In stages, each usable on its own:

1. **Anchored objects.** A picture, shape, table or text frame placed in the text, so it moves when the text
   reflows: *inline* (it sits on the line, which grows to hold it, with an offset up or down), or *custom*
   (placed against its anchor's line, by the frame's edges or the anchor itself, such as an icon in the margin
   beside its paragraph, with text wrapping round it if it asks). Selected, styled and changed like any object;
   deleted with its anchor; copied with the text.
2. **Cross-references.** A reference to a heading, or to any place in the text given a name, shown as its page
   number, its text, or both (`see "Tide tables" on page 12`), kept up to date as pages move, and a link in the
   PDF.
3. **An index.** Entries marked in the text (a topic, and a sub-topic under it; or *see* another topic), then an
   index made from them: alphabetical, with a heading for each letter, page numbers joined into ranges, in index
   styles, flowed into a frame like a table of contents, and made again when asked.
4. **Find and replace with patterns.** Regular expressions in the find bar (with groups and `$1` in the
   replacement), and finding by paragraph or character style.

**Status (12 November 2026):** done. Stage 1: an anchored object is kept in its story (`Story.Anchored`) at a
marker character (`Fields.Anchor`, whose format names it), measured into its line by the layout (inline) or placed
against it afterwards (custom; a story is laid out twice when one wraps text), and drawn with its frame's text.
`Document.AllObjects` includes them, so they are found, saved, styled and preflighted as any; the page view picks
those set on its sheet. Not done: anchoring in table cells and footnotes, and objects that are anchored and also
in groups.

Stage 2: a named place is a marker (`Fields.Place`, `CharFormat.Mark`); a cross-reference a field
(`Fields.Reference`, `CharFormat.Ref` = "name|kind"). `References.Resolve` works out what a story's references show
from the layouts, and `Layouts.Of` lays a story out again until its references to itself stop changing (each story
being worked out gives its last layout to the others, so there is no endless loop). Referring to a heading names
its start. PDFs get a named destination for each place and a link for each reference.

Stage 3: an entry is a marker (`Fields.IndexEntry`, `CharFormat.Index` = "topic, sub-topic, see" separated by
tabs). `Indexes.Entries` finds each one's page from its story's layout (a footnote's from its reference's),
`Indexes.Paragraphs` makes the index (topics grouped regardless of case and spelt as most of their entries spell
them), and `Editor.InsertIndex` places it with its styles in one undo step. Not done: an index that updates itself
(it is made again by hand, like the contents), and sub-sub-topics.

Stage 4: `FindOptions` has `Regex` (.NET's regular expressions, `^` and `$` at paragraph ends, a two-second
timeout) and `ParaStyle` and `CharStyle`; `Search.Problem` says what is wrong with a search, `Search.Replacement`
fills a pattern's groups in (with `\n` and `\t`), and with nothing to find, each stretch of text in the style is a
match. The find bar has a Pattern box and a style list. Not done: replacing with a style (changing the look of what
is found), and finding across paragraphs.
