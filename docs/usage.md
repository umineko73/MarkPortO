# Detailed usage

[README](../README.md)

## Usage

### Desktop app

1. Add files or a folder (or drop them on the list).
2. Choose an output folder.
3. Set the **Conditions**.
4. Press **Start**.

While a conversion runs, the conditions cannot be changed. The values are saved to `%APPDATA%\MarkPortO\settings.json` and restored at the next start.

While the list of files is read, **Working** is shown at the bottom right. When the screen takes longer than 0.3 seconds before it can be used again (a large folder was added, the output folder was changed, and so on), a message such as **Reading the files…** is shown in the middle of the window, and the window cannot be used until it ends. It is not shown while a conversion runs (so that it can be interrupted).

#### Presets

**Preset**, at the top of the **Conditions**, puts the recommended settings of Step1 to Step3 for a purpose. They can still be changed afterwards; the name then shows "(changed)".

| Preset | For | Main settings |
|---|---|---|
| For reading (`human`) | Moving from Office documents to Markdown | bold and italic written, pictures and shapes saved, no page images in the Markdown, Step3 adds what the figures show, no description at the top |
| For RAG (`rag`) | Documents read by a language model | no bold and italic, pictures and shapes saved, the page images as comments (a passage can be traced to its page), Step3 adds what the figures show, the description at the top, the chunks for RAG (`_chunks.jsonl`) |

- Both use the tables of the extracted text, leave out repeated tables, and write the VLM results into the Markdown of the document.
- Step3 is turned on only when a VLM connection is chosen, so a preset never stops a conversion on a machine without one.
- The inputs, the output folder, the engine, the VLM connection, the kinds of file and the range of a sheet do not change.
- **Save…** saves the settings now as a preset with a name; **Manage…** renames and deletes them. The built-in presets cannot be changed or deleted.
- On the command line, `--preset human|rag|<name>`; the options given with it win over it.
- The name of the preset is in the comment at the top of the Markdown (`<!-- converter: …; preset: rag -->`).

The **Conditions** have a part that can be folded (**Kinds of file and options**) and the rows of Step1 to Step3. What is chosen for each run is in the rows of the steps; what is set once is in the part that can be folded.

| Where | Item | What it sets |
|---|---|---|
| Kinds of file and options | Kinds of file | The kinds of file in a folder, whether sub-folders are included, and the output of a folder (make the folder, or contents only) |
| 〃 | Extractor | Who extracts the text of each kind: built-in or an external command (shown when a command is registered) |
| 〃 | Extraction options | The range of an Excel sheet, a file for each sheet, and so on (grey when an external command is chosen) |
| 〃 | Details of the steps | Step1: the engine, the DPI, bold and italic. Step2: how the page image is written. Step3: write the results to a separate file, tables, leave out repeated tables. Markdown: write "About this Markdown" at the top, write the chunks for RAG (`_chunks.jsonl`) |
| Rows of the steps | Step1 | Save the pictures of the document, cut out the shapes, group the shapes that are near |
| 〃 | Step2 | Whether the page images are written in the Markdown |
| 〃 | Step3 | Reading with the VLM (a model that reads images), the use of the VLM, the connection, the prompts, the page range |

### The output of a folder, and a file for each sheet

- **Output of a folder** (in the row "Kinds in a folder"; `--folder-layout` on the command line): when a folder is added, "Make the folder" (the default) makes a folder of its name in the output folder and writes into it. "Contents only" makes no such folder: the files and sub-folders in it go straight into the output folder. When several folders have documents of the same name, they are numbered, like `name (2)`. Files added one by one always go straight into the output folder.
- **A file for each sheet** (in the row Excel; `--excel-per-sheet on`): one Markdown for each sheet, `<book>_<sheet>.md`, in the folder of the book, and no `<book>.md`. The page images and the pictures are shared, and the page numbers are those of the whole book. The separate file of the VLM results and the chunks for RAG are split by the sheets too. Not for an xlsx that an extractor converts. "Open Markdown" in the list opens the output folder.

```text
out/report/              # with "A file for each sheet"
├── report_Sales.md
├── report_Costs.md
└── images/              # page-001.png (Sales), page-002.png (Costs) ...
```

For one file, a **page range** (like `3-5,8`) limits the pages that Step3 reads.

Folded, **Kinds of file and options** shows in one line what is chosen (the engine and the DPI, and the other settings that are not the usual ones); the rows of the steps stay in view. The list of files and the list of pages share the height; drag the bar between them to change it.

The **State** of a file has a coloured mark: green done, orange some pages failed, red failed, the accent colour of Windows running, grey waiting or interrupted. While Step3 reads the pages of a file, a bar under its Step3 column shows how far it is. **VLM usage** shows the total of the tokens; point at it for the parts (prompt and completion). During a run, the bottom of the window shows how many files are finished.

### Reading with the VLM (Step3)

Step3 adds what a VLM reads in the images to the text that Step1 took from the document. The items of Step3 are:

| Where | Item | What it does |
|---|---|---|
| Row of Step3 | Extract text from print images with the VLM | Turns Step3 on or off. When it is off, the other items are hidden. |
| 〃 | Use of the VLM | **Add to the text** (the default) or **Read every page**. See below. |
| 〃 | Connection | The VLM connection to use. Set them up with the gear button beside it (**VLM connection settings…**). |
| 〃 | Prompt | The prompt for each kind of file. Edit them with the pencil button beside them (**Prompt settings…**). |
| Details of the steps | Write the results to a separate file (name_vlm.md) | The results of the VLM go to `name_vlm.md`, not into the Markdown of the document (`name.md`). |
| 〃 | Tables | What is done with the tables that the VLM wrote, on a page that it read whole. See "Tables and repeated tables" below. |
| 〃 | Leave out repeated tables | On a page that the VLM read whole, a table that it wrote twice or more is written once. |

The VLM is sent a page image (or the image of a figure) and the text that Step1 took from the document. With a connection to a service outside your network, the images and the text of your documents leave the network.

#### Use of the VLM: add to the text, or read every page

| Use | What it does | When to use it |
|---|---|---|
| Add to the text (default) | The text, tables and code of Step1 are kept as they are. The VLM reads only what Step1 does not have (what the figures show) and adds it. | Word, PowerPoint and Excel documents. When the text must stay exact. When the VLM should take less time and cost less. |
| Read every page | The VLM reads every page image and writes out the whole page (what Step3 did up to 0.3.6). | Documents that Step1 cannot extract well. When the result should come from the VLM only. |

**Add to the text** is recommended. When a VLM writes out a whole page, it can misread characters that Step1 had right. With **Add to the text** the VLM never writes the text again, so this does not happen. A figure is sent on its own, not as a small part of the page, so the values of a chart and so on are read more exactly.

#### How a page is read with "Add to the text"

With **Add to the text**, each page is read in one of these ways:

| The page | How it is read | "VLM" column of the page list |
|---|---|---|
| It has figures that Step1 saved (pictures, cut-out shapes) | Each figure is sent on its own, with the text before and after it. | Done (figures) |
| It has drawn shapes of Word (boxes, circles, arrows) that are not cut out | They are not saved as pictures, so the page image is sent once and the VLM is asked to read the shapes only. The text of the page is sent with it, so that the VLM does not write it again. | Done (figures) |
| The text of Step1 is enough (no figure) | Not sent. | Not sent (the text is enough) |
| It has only the rest of the page before (the end of a table row or of a paragraph pushed over the page break) | Not sent. Step1 writes that part with the page before. | Not sent (the text is enough) |
| Step1 has almost no text of it (fewer than 20 characters: the text is in an image or a shape, and so on) | The whole page is written out. | Done |
| It has a picture that could not be saved (only its alternative text) | The whole page is written out. | Done |
| A PDF, or a document that an external command extracted | The whole page is written out (Step1 makes no tables or headings for them). | Done |

These pages are never sent, whichever the use: deleted pages (an X over the page), blank pages, pages outside the page range, and, when **Tables** is **use the extracted text** or **do not keep in the VLM result**, a page with only the rest of a table of the page before ("Joined table").

For a page that was not sent, the **VLM usage** column says "none (not sent)". When no page of a document was sent, the file list says "none (not sent)" too. "unavailable" means that a page was sent but the VLM did not tell its usage.

#### Where what was read in a figure is written

What the VLM read in a figure is written as a quote right after the figure in the Markdown. When the paragraph right after the figure is its caption (one short line such as "Figure 1 …" or "図1 …"), it is written after the caption. What was read in the shapes of a Word page is written after the paragraph that the shapes are with.

```markdown
![chart](pictures/page-001-1.png)

Figure 1. Response time by month

<!-- vlm:figure picture=pictures/page-001-1.png -->
> **What the VLM read in the figure**
>
> - July: 196 ms
> - August: 183 ms
<!-- vlm:figure-end -->
```

Nothing is written for a figure when the VLM answered that it has nothing to add (`NONE`). A page that was written out whole is between `<!-- vlm:begin page=N -->` and `<!-- vlm:end -->`, as before.

With **Write the results to a separate file**, `name_vlm.md` has the text of Step1 and the figures also on the pages that the VLM added to or did not read, with what was read in the figures in the same form. Read alone, `name_vlm.md` has the text and the figures.

#### Settings for "Add to the text"

- **Turn on "Save the pictures of the document" in Step1.** When it is off, the pictures of the document are not saved as files, so they cannot be read as figures. A picture with an alternative text leaves only `![alternative text]`, and its page is written out whole; a picture without one leaves nothing, and is not read.
- The shapes of PowerPoint and Excel (groups, and shapes of half an inch or more) are cut out of the page image as pictures with **Cut out the shapes** in Step1, and are then read as figures.
- The shapes of Word (shapes, groups, an inline drawing canvas, and shapes in table cells) are cut out too with **Cut out the shapes**. A shape placed from its paragraph or line is looked for in the drawing of the PDF, near the text of its paragraph in the PDF. When it is not found for sure (nothing is drawn near the paragraph, drawings side by side cannot be told apart, and so on), it is not cut out, and the pictures in it (in a table cell, also the text of its text boxes) are written as they are. With the setting off, the shapes of Word are still read from the page image. Shapes of the old kind (VML, drawn in a document in compatibility mode) are not cut out (their straight lines are read for the "×" of a deleted page).
- With **Group nearby shapes** on, the shapes and pictures within 24 points of one another up and down (`input.shapeGroupGapPoints` in `settings.json`), and those on the same row however far apart across, are cut out as one picture: in PowerPoint on a slide, in Word on a page (the shapes of one paragraph that are placed the same way, always; a shape drawn alone is anchored in a paragraph of its own, so a diagram in several paragraphs is joined too), in Excel on a sheet (the shapes, with the small lines and arrows between them). Off, each shape is cut out alone, and the pictures stay as they are.
- The instruction for the figures is in the **Figures** tab of the prompt settings (one for every kind of file). `{pagetext}` in it is replaced by the text before and after the figure. The prompt of each kind of file is used when a page is written out whole.

#### Tables and repeated tables

**Tables** and **Leave out repeated tables** apply **only to a page that the VLM read whole**: every page with **Read every page**, and with **Add to the text** the pages that are written out whole (PDF, pages that Step1 has almost no text of, and so on).

Suppose a Word page contains a price table and a screenshot, and Step1 extracts both. With **Add to the text**, the Markdown keeps the Step1 price table and adds what was read in the screenshot after it. Selecting **use the VLM result** does not send the price table to the VLM. If what was read in the figure contains a table, it stays with the figure.

| Tables setting | Markdown for a page read whole |
|---|---|
| use the VLM result | Include the table the VLM read from the page image. A table that runs over pages is read as one, on the page where it starts. |
| use the extracted text | Where Step1 extracted a table on this page, replace the VLM tables with the Step1 tables. |
| do not keep in the VLM result | Where Step1 extracted a table on this page, omit the VLM tables and add a note pointing to the tables in the document text. |

If Step1 extracted no table on that page, all three settings keep the tables the VLM read.

**Leave out repeated tables** then checks the VLM output for that page. If the model wrote the same price table twice, the Markdown keeps the first copy and marks the omission. This applies to tables with at least two data rows. If the price table appears once in the document text and once in the VLM result, choose **do not keep in the VLM result** to omit the second one.

#### After changing the settings

After the use of the VLM, the connection (the model) or a prompt is changed, **Run Step 3 again** reads again the pages that were read with the earlier settings. Step1 and Step2 are not done again.

The mark that lets Step3 read the shapes of a Word page is made by Step1. To have the shapes of a document converted before this feature read, press **Convert again from Step 1**.

### Checking the results

**Preview** (double-click a file): choose which of the page image, the extracted text and the VLM result to show, side by side or stacked, drawn or as source. **Follow** shows the page read last.

To preview a specific page, select the document in the file list of the main window. In the page list below it, right-click the row you want (for example, page 9) and choose **Preview**. The preview opens on that page. If the document already has a preview window, that window moves to the selected page. Opening a specific page turns **Follow** off, so you can keep reading during a conversion; turn **Follow** on again to track the progress.

| Action | What it does |
|---|---|
| Ctrl + wheel | Zooms the page image |
| Ctrl + 0 | Zoom back to 100% |
| Left and right arrow keys | Change the page |
| Mouse wheel | Scrolls, stops at the end of a page first, then changes the page |
| Drag with the right button | Scrolls |
| ◀ ▶ (Ctrl+Left, Ctrl+Right) | The previous and the next file of the list |

The zoom and the scroll position are kept when the page changes or a new result arrives.

#### Reading pages again with the VLM

1. Tick the check box of a page in the page list of the preview (or use **Mark this page** and **Mark pages that could not be read**: a page fails when the VLM gives no answer, times out, or the connection or the server gives an error). The right-click menu of the file list (**Mark for running Step 3 again**) marks every page of the selected files. Marked pages show "Waiting to run again" in the Step3 column of the file list and in the VLM column of the page list.
2. Press **Run Step 3 again** on the main screen. Only the marked pages are read again, with the connection and the prompts chosen on the main screen, and the results are replaced.
3. A page that fails again keeps its earlier result and its mark.
4. After the connection, a prompt or the use of the VLM is changed, **Run Step 3 again** can be pressed without marks: it reads again the pages that were read with the earlier settings.

![The preview window](images/preview.png)

The list of files shows the folder of each document in the output (the **Output** column). The right-click menu opens that folder, the Markdown, or the Markdown of the VLM results.

### Opening a past output

1. Choose **File > Open a past output...**.
2. Choose the output folder, or the folder of one document.
3. Choose a document in the list. A file converted more than once has its results (newest first) in the box below.
4. Press **Preview**, **Open in Explorer** or **Open**.

Documents and results are listed newest first. **Open** puts the selected files in the inputs and the file list of the main window and makes this the output folder; it does not start a conversion.

Several documents can be selected (Ctrl+click, Shift+click): the newest result of each is used. **Preview** is for one document only.

Opening an output only reads it. The output folder can be moved or copied to another place and still opens, as long as `.markporto` goes with it. Without `.markporto` (only the folders of the documents were copied, for example), the `conversion.json` of each document folder is read: the documents can be previewed and opened (as a new conversion), not resumed.

### Resuming an interrupted conversion

1. In **Open a past output**, choose the result to resume.
2. Press **Open**.
3. Press **Resume** in the main window.

A result with failures, and an interrupted one that was given up when a later conversion was started, can be chosen too. When several documents are opened, the run can be resumed if a document that did not finish is in it.

### Going back to the earlier state

When the files and folders to convert are chosen, the screen goes back to their earlier state.

- **File list**: each file of the inputs is shown with the state of its last conversion in the output folder, also when the set of inputs differs from last time (folders A and B then, only A now).
- **Output folder**: the output folder of each input is remembered. When the output folder set now has no record, it is switched to the one the first input was converted into (the status says so). An output folder you choose yourself is kept until the inputs change.
- **Conditions**: those of the newest conversion of the first input are put back; the preset shows **Earlier conditions**. Choosing inputs does not save them: they are saved when you change a condition or start a conversion. Emptying the inputs brings the saved conditions back.

"The first" is the first input, from the top of the list, that has a record. Adding files or folders whose earlier output folder or conditions differ from the first asks first; when added, the output folder and the conditions stay those of the first, and the files without a record in its output folder are converted anew.

**File > Past history** lists the remembered output folders (the input, the output folder, when, and those whose folder is gone); the ones chosen, or all of them, can be forgotten. **Go back to this** (or a double click on a row) puts the screen back to that input and its output folder (the conditions come back from its records). The results of the conversions stay.

### Converting again

| State in the list | Meaning |
|---|---|
| Not converted | No record in this output folder yet |
| Done, Done (some pages failed), Failed | The result of the last conversion |
| Not finished | Interrupted, or it ended halfway |
| Step2 not run, Step3 not run | The last conversion had that step off; it is on now |
| Changed since the conversion | The file changed after its last conversion |
| Running elsewhere | Another window or the command line converts it (no action takes it) |

- **Start**: when every file is "Not converted"; converts every file of the inputs.
- **Resume**: goes on file by file: a file not finished goes on, failed files and pages are tried again, a step that is on now but was not then is run alone, and files not converted are converted with the conditions on the screen. Several earlier conversions go on in turn in one **Resume**.
- **Redo all**: converts every file of the inputs again with the conditions on the screen.
- **Reconvert updated files**: converts the changed files and the new ones, into the folders of their earlier conversion.

#### Adding Step3 to files converted up to Step2

1. Choose the inputs of then (the list and the conditions go back to their earlier state).
2. Switch Step3 on and choose the VLM connection. The list shows **Step3 not run**.
3. Press **Resume**. Step1 and Step2 are not done again: only Step3 runs, in the earlier folders, and the Markdown is written again.

A step switched off now that was on then leaves its results. A file that changed is converted again from Step1 instead.

Where **Start** and **Redo all** write a file that was converted before is chosen beside the buttons, under **If the folder exists** (**Make a new one** or **Overwrite**).

To convert some files again, select them in the file list and press **Convert again from Step 1**, or choose **Convert the selected files again from Step 1** from the right-click menu. **The Markdown and the page images in the earlier folder are replaced**, so the paths stay the same.

### Work sets

The inputs, the output folder and the conditions can be saved under a name and opened later: **File > Save work set…**, **Open work set** and **Manage work sets…**.

- Opening one first resets the inputs, the output folder and the conditions, then puts in those of the work set; the conditions are saved.
- The list shows the earlier state of its files; the status says where its conditions differ from the earlier conversion.
- Only the name of the VLM connection is kept: when there is no connection of that name any more, choose one again.
- Inputs that are gone are left out.

### VLM connections and prompts

They are in the **Settings** menu. Each kind of file can have several named prompts (a built-in one for a small local model and one for a cloud LLM are included); choose one on the main screen or with `--prompt <name>`.

A prompt can contain `{pagetext}`, the text Step1 took from the page (it is sent with the image). The **Figures** tab is the instruction for reading a figure with **Add to the text** (one for every kind of file; `{pagetext}` is the text before and after the figure).

![VLM connection settings](images/connections.png)

### Command line

```powershell
markporto-cli convert D:\docs\report.docx --out D:\out
markporto-cli convert D:\docs --out D:\out --types docx,xlsx --recurse
markporto-cli convert D:\docs --out D:\out --step3 on --settings settings.json   # read with a VLM
markporto-cli convert --resume --out D:\out                                      # continue an interrupted run
markporto-cli list-jobs --out D:\out
markporto-cli check-connection --settings settings.json
```

| Command | What it does |
|---|---|
| `markporto-cli convert <paths...>` | Converts files and folders. |
| `markporto-cli list-jobs --out <folder>` | Lists the jobs left in the output folder (to choose one with `--resume --job`). |
| `markporto-cli check-connection [--connection <name>] [--image]` | Checks that the VLM connection answers and has the model; `--image` also checks that it reads an image. |
| `markporto-cli list-connections` | Lists the VLM connections of `settings.json` (no access key is shown). |
| `markporto-cli consent <name> [--revoke]` | Agrees that a connection or an extractor sends documents outside the local network (`--revoke` withdraws it). |

`--settings <file>` (the path of `settings.json`; the default is `%APPDATA%\MarkPortO\settings.json`) works with every command. The options of `convert` override `settings.json`; an option that is not given keeps the setting.

**Where and what**

| Option | What it does |
|---|---|
| `--out <folder>` | The output folder. |
| `--types docx,xlsx,pptx,pdf` | The kinds of file to convert in a folder (comma-separated). |
| `--recurse` / `--no-recurse` | Include sub-folders, or not. |
| `--changed-only` | Only the files that changed since their last conversion, the new ones and the ones that did not finish, into their earlier folders. |
| `--same-folder on\|off` | on: converts every file and writes those converted before into the folders of their earlier conversion. off: makes a new folder for each file. |
| `--pages <range>` | For one file: the pages that Step3 reads, like `3-5,8` (`8-` is from page 8, `-4` is up to page 4; write `--pages=-4`). Step1 still converts the whole file. |

**Reading the document (Step1)**

| Option | What it does |
|---|---|
| `--engine office\|libreoffice` | The engine that prints the page images. |
| `--dpi <72-600>` | The resolution of the page images. |
| `--folder-layout folder\|contents` | Where the output of a folder goes: a folder of its name (the default), or what is in it straight into the output folder. Not with `--resume`. |
| `--excel-per-sheet on\|off` | Write an Excel book as one Markdown for each sheet, `<book>_<sheet>.md` (the default is off). |
| `--excel-range columns\|sheet\|print-area` | How an Excel sheet is cut into pages: every column on one page if possible / the whole sheet on one page / its print area. |
| `--include-hidden` / `--no-include-hidden` | Include the hidden sheets and slides, or not. `--include-hidden-sheets` and `--include-hidden-slides` (and their `--no-` forms) set only one of them. |
| `--include-off-slide` / `--no-include-off-slide` | Write the text of the shapes outside a slide (the default), or not (pptx). |
| `--emphasis` / `--no-emphasis` | Write bold text between `**` and italic text between `*` (the default), or not. |
| `--pdf-text on\|off` | Use the text layer of a PDF as the text of its pages (it also helps the VLM; the default is on). |
| `--pictures on\|off` | Save the pictures of a Word, PowerPoint or Excel file in `pictures/` (the default is the settings file; off at first). Turn it on for Step3 to read figures with **Add to the text**. |
| `--shapes on\|off` | Cut the drawn shapes of a Word, PowerPoint or Excel file out of the page image into `pictures/` (the default is the settings file; off at first). |
| `--group-shapes on\|off` | With `--shapes on`: cut the shapes and pictures of a PowerPoint file that are near one another, or on the same row, and those of a Word page and the shapes of an Excel sheet in the same way (in Word also those of several paragraphs), out as one picture (the default is on). |
| `--extractor-docx\|xlsx\|pptx\|pdf <name>` | The extractor that writes the Markdown of that kind of file (`builtin` is the built-in one). |

**Step2 and Step3 (VLM)**

| Option | What it does |
|---|---|
| `--step2 on\|off` | Make the page images and write them in the Markdown. |
| `--page-image image\|reference\|link` | How the page image is written (with Step2). `reference` (the default) is a comment `<!-- page-image: images/page-001.png -->` that a Markdown viewer does not show but a program or a model can read; `image` is `![Page 1](images/page-001.png)`, which a viewer shows; `link` is `[Page 1](images/page-001.png)`, a link that a viewer shows instead of the image. |
| `--preset human\|rag\|<name>` | Use a preset (the recommended settings of Step1 to Step3). The options given with it win over it. |
| `--structure-note on\|off` | Write "About this Markdown" at the top of the Markdown (the default is on). |
| `--chunks on\|off` | Write the chunks for RAG, `<name>_chunks.jsonl` (the default is off; on with the preset `rag`). See "Chunks for RAG" below. |
| `--step3 on\|off` | Read with the VLM (how: `--step3-mode`). |
| `--step3-mode supplement\|transcribe` | How Step3 uses the VLM. supplement (the default): keep the text of Step1 and read only the figures, and add what they show (a page that Step1 has almost no text of, and a PDF, are written out whole). transcribe: write out every page. See "Reading with the VLM (Step3)". |
| `--connection <name>` | The VLM connection to use. |
| `--prompt <name>` | The prompt for every kind of file that has a prompt of this name (or `standard`). Not used with `--resume`. |
| `--prompt-docx\|xlsx\|pptx\|pdf <name>` | The prompt for one kind of file. |
| `--vlm-separate on\|off` | Write the results of Step3 to a separate file, `name_vlm.md`. |
| `--step1-tables off\|replace\|omit` | On a page read whole, where the text of the page has a table: keep the VLM tables (off) / replace them with the tables of the text (replace) / leave them out (omit). **Tables** on the screen. |
| `--drop-repeated-tables on\|off` | On a page read whole, leave out the tables that the VLM wrote again. |
| `--max-pages <number>` | Stop after Step3 has read this many pages (pages that were not sent are not counted). |
| `--max-cost <US dollars>` | Stop when the cost of Step3 reaches this (when the command tells the cost). |
| `--parallel <number>` | How many external commands run at the same time. |

**Running and the result**

| Option | What it does |
|---|---|
| `--resume` | Continue an interrupted run (with `--out`). |
| `--job <id>` | With `--resume`: the job to continue (see `list-jobs`). |
| `--retry-failed` | With `--resume`: convert the files that failed again. |
| `--result <file>` | Write the result as JSON to this file. |
| `--quiet` | Show less progress. |

Exit codes: `0` success, `1` some files failed, `2` invalid arguments or settings, `3` the checks before the run failed (including a missing consent), `4` stopped by a usage limit, a login or a limit of pages or cost (use `--resume`), `130` interrupted.

### `settings.json`

```json
{
  "schemaVersion": 1,
  "input": { "paths": ["D:\\docs"], "recurse": true, "types": ["docx", "xlsx", "pptx", "pdf"], "includeHiddenSheets": false, "includeHiddenSlides": false, "includeOffSlideText": true },
  "output": { "folder": "D:\\out" },
  "ui": { "language": "system", "theme": "system" },
  "engine": { "kind": "office" },
  "images": { "dpi": 150 },
  "steps": { "step2": true, "step3": true, "step3Mode": "supplement" },
  "vlm": {
    "selected": "local",
    "connections": [
      { "name": "local", "baseUrl": "http://localhost:1234/v1", "model": "vision-model", "apiKeyEnv": "MARKPORTO_API_KEY", "timeoutSeconds": 120 }
    ]
  }
}
```

The location can be changed with the environment variable `MARKPORTO_SETTINGS`. `input.shapeGroupGapPoints` is how near (in points) the shapes and pictures of a slide (up and down), of a Word page (a diagram drawn in several paragraphs) or of an Excel sheet, are to be cut out as one picture with **Group nearby shapes**; those on the same row are one picture however far apart (the default is 24, from 0 to 720); it is not on the screen. Write the *name* of an environment variable (`apiKeyEnv`) rather than a key. A key typed in the app is stored so that only the current Windows user can decrypt it (DPAPI), and never appears in logs or output.

### An external command writes the Markdown

For a kind of file, the Markdown can be written by a program that you installed yourself (MarkItDown, Docling, Pandoc and so on). MarkPortO does not install or update it; it only runs it, without a shell, with the arguments that you give.

#### PDF files

The built-in extraction of MarkPortO takes the text of a PDF simply: headings run into the text, and a table is a line of words. For PDF files, MarkItDown or Docling is recommended.

- MarkItDown: fast; it keeps headings, lists and line breaks well.
- Docling: makes the best tables and reads scanned PDFs too; it takes longer.
- Pandoc cannot read PDF files.

With a command, the page images, Step3 (the VLM) and the preview still work. When the command gives no text for a scanned PDF, the VLM reads it if Step3 is on (see "How it differs from the built-in extraction" below).

#### Registering a command

1. Open **Settings > Extractor settings**.
2. Enter a name, the command and its arguments. **Template…** adds the usual settings of MarkItDown, Docling or Pandoc.
3. Set a time-out and environment variables if needed.
4. Press **Try…** to convert one document and check the result.
5. Choose the command for each kind of file in the **Extractor** row of the conditions.

On the command line, use `--extractor-docx|xlsx|pptx|pdf <name>` (`builtin` is the built-in one). The commands can also be written in `settings.json`, as below.

#### How it differs from the built-in extraction

| Step | With an external command |
|---|---|
| Step1 | The page images are saved as always; the command writes the text |
| Step2 | The page images are not put into the Markdown of the command |
| Step3 | The page images are read as usual (always whole), and the results go to `name_vlm.md` |

The result is the Markdown of the whole document, as the command wrote it, **with no page marks and no page image references** (the page images are still saved in `images/`). The features of MarkPortO for that kind of file (tables over several pages, merged cells, bold and italic, and so on) do not apply, and the options beside a kind of file (hidden sheets, the range of a sheet and so on) are grayed out while a command is chosen.

When the command gives no text at all (a scanned PDF, for example) and Step3 is on, the file does not fail: the VLM reads the page images. The Markdown then says so, and the text is only in `name_vlm.md`. With Step3 off, the file fails.

It has been tried with MarkItDown, Docling and Pandoc on sample files, not with every tool, version or kind of document, and the results of the tools differ.

#### Example

```json
"extractors": [
  { "name": "markitdown", "command": "markitdown", "args": ["{input}", "-o", "{output}"] },
  { "name": "docling", "command": "docling", "args": ["{input}", "--to", "md", "--image-export-mode", "placeholder", "--output", "{outdir}"], "timeoutSeconds": 600 }
],
"input": { "extractors": { "pdf": "docling" } }
```

| Value | Meaning |
|---|---|
| `{input}` | The input file |
| `{output}` | The Markdown file to write |
| `{outdir}` | A folder for the command |

When only `{outdir}` is given, the `.md` file that the command writes there is read. `"output": "stdout"` reads the standard output.

The command is found in the `PATH` (or give the full path). The arguments differ between versions of a tool: try the command by hand first. Examples of installing:

```powershell
uv tool install "markitdown[all]"
uv tool install docling
winget install JohnMacFarlane.Pandoc
```

Pandoc reads docx and pptx well. It does not read PDF, and it writes a sheet of xlsx as a plain grid of cells.

A command that sends your documents outside the PC needs `"sendsExternally": true` and a consent: press **Agree...** in the extractor settings, or run `markporto-cli consent <name>` (`--revoke` withdraws it).

### Reading with `claude`, `codex` or `opencode`

```json
{ "name": "cloud", "kind": "command", "command": "claude", "model": "haiku", "effort": "low", "parallel": 2, "maxPages": 200, "maxCostUsd": 3.0 }
```

For each image the command is run once, in an empty temporary folder, **without your own settings, hooks, memory or tools**. The conversion stops (and can be resumed) at a limit of pages or cost, a usage limit, a missing login, or three failures in a row.

**The images, and the text of the pages, are sent to the service behind the command, outside your network, and charges may apply.** Do not use this for confidential documents. Such a connection (and an OpenAI-compatible one outside the local network) is used only after you agree: press **Agree...** in the connection settings, or run `markporto-cli consent <name> --settings settings.json` (`--revoke` withdraws it).

## Output

```text
out/report/
├── report.md          # starts with the source file name and a description of the structure
├── report_vlm.md      # only with --vlm-separate on: the results of Step3 (with "Add to the text", also the text and what was read in the figures)
├── report_chunks.jsonl # only with the chunks for RAG: the chunks for search, one on each line
├── report.log         # what was made of each page (size, image, deleted or blank page), the warnings and the failures
├── conversion.json    # the record of the conversion: the source, the conditions, the state, the files written (below)
├── images/            # the page images (Step2)
│   ├── page-001.png
│   └── page-002.png
└── pictures/          # the pictures of the document and the cut-out shapes (with "Save the pictures" or "Cut out the shapes")
    └── page-001-1.png
```

A page that a "×" drawn with shapes covers nearly whole (70% or more each way; in Word 60% or more) is a deleted page: its text and image references are not written, only a quote that starts with `> **[Deleted page]**`. The "×" is one multiply shape, or two diagonal lines that cross. In Word, a "×" of straight lines of the old kind (VML, drawn in a document in compatibility mode) is taken too; of the pages where the paragraph of the shapes can be, the page where the PDF draws a "×" is the deleted page; when none does, no page is deleted (so that no page is lost). A "×" of text, a small one, or one drawn freehand is not taken (a freehand drawing over the page is warned about, and the page is converted as usual).

Merged table cells are written in the notePM style (`<` joins the cell on the left, `^` the cell above). Text with a strikethrough is written as `~~text~~`. A `<!-- page: N -->` line starts each page.

### The record of the conversion (`conversion.json`)

Each document folder tells how its document was converted: the folder alone says what was converted, with which conditions, and how far it got.

- What it holds: the source file (its full path, its path from the folder that was added, its size and time), the output folder, the conditions (engine, DPI, Step1 to Step3, preset ...), the state (Step1 to Step3 and the whole: running, completed, failed, interrupted), the number of pages, and the files written (with a workbook written per sheet, each sheet and its pages).
- When it is written: when the conversion of the document starts, when each step ends, when it ends or is interrupted; again on a resume or a new conversion.
- No API key, no path of an external command, no text of a prompt (the model of the connection and the names of the prompts only).
- What a resume uses is the state in `.markporto` of the output folder; `conversion.json` is a copy of it.
- **Giving the output to someone gives them the path of the source files (the names of your folders) too**, as the first line of `report.log` does.

```json
{
  "schemaVersion": 1,
  "source": { "path": "D:\\Designs\\v1.2\\design.docx", "relativePath": "v1.2/design.docx", "kind": "docx" },
  "status": "completed",
  "steps": { "step1": "done", "step2": "done", "step3": "notApplicable" },
  "conditions": { "preset": "rag", "engine": "office", "dpi": 150, "step3": false }
}
```

(An excerpt.)

### Chunks for RAG (`_chunks.jsonl`)

With "Write the chunks for RAG (_chunks.jsonl)" on in "Details of the steps" (on in the preset For RAG; `--chunks on` on the command line), a file `<name>_chunks.jsonl` is written next to the Markdown: the document cut into chunks for search (RAG). It is written again each time the Markdown is (after Step3, a re-reading, a resume).

- One JSON object on each line (JSON Lines, UTF-8 without BOM, LF).
- A new chunk starts at each heading; paragraphs are put together up to 1,500 characters. A table or a code block is a chunk of its own and is not cut in the middle (a long table is cut between its rows, and every part starts with the header row). A table or code over several pages is one chunk.
- What the VLM read in a figure (Step3, "Add to the text") is in the chunk of the figure. A page without text (a scan) becomes a chunk of what the VLM read.
- "About this Markdown", the table of contents, deleted pages and the comments of the page separators are left out.

| Item | Content |
|---|---|
| `schemaVersion` | The version of the format (`1` now) |
| `id`, `prev`, `next` | The ID of the chunk (from `c0001`) and of the chunks before and after it |
| `source`, `sourceModified` | The name of the source file (without its folders) and when it was last changed (UTC) |
| `path` | Where the document is in the folder that was converted (for example `v1.2/basic design/design.docx`); the file name for a file added on its own |
| `markdown` | The name of the Markdown file in the same folder |
| `headings` | The headings the chunk is under (for example `["2. Data model", "2. Jobs and resuming"]`) |
| `kind` | `text`, `table`, `code`, `slide` or `page` |
| `pages`, `pageImages` | The pages (for example `[6, 7]`) and their page images; `null` and empty for a document of an extractor |
| `sheet` | The name of the sheet (xlsx only) |
| `text` | The text of the chunk (Markdown) |

An example in Python: read the chunks and show where a chunk that was found comes from. Give the source of an answer from `source` and `pages`, not from the model (a small model can write a page that does not exist).

```python
import json
chunks = [json.loads(line) for line in open("report/report_chunks.jsonl", encoding="utf-8")]
by_id = {c["id"]: c for c in chunks}
for c in chunks:
    if "resume" in c["text"]:
        pages = "-".join(map(str, sorted(set(c["pages"])))) if c["pages"] else "?"
        print(f'{c["source"]} p.{pages} / {" > ".join(c["headings"])}')
        # Give the chunks before and after it too, when a section is cut in two: by_id[c["prev"]], by_id[c["next"]]
```

### Searching from Claude Code and similar tools

Register the small MCP server `markporto-rag` (in `tools\markporto-rag\` of the ZIP) in a tool such as Claude Code, and its model can search the `_chunks.jsonl` files and answer with the source (the original file, the pages, the headings and the page images). No database is needed.

1. Convert with the chunks for RAG on (the section above; on in the preset For RAG).
2. Install [uv](https://docs.astral.sh/uv/) (`winget install astral-sh.uv`). The MCP package is fetched by uv the first time (this needs the internet).
3. Make the configuration `%APPDATA%\MarkPortO\markporto-rag.json` (copy `tools\markporto-rag\markporto-rag.sample.json` and edit it). The smallest one has only the folders to search:

   ```json
   { "roots": ["D:\\MarkPortO-out"] }
   ```

   By default the server also connects to the embedding model `text-embedding-multilingual-e5-small` of LM Studio (`http://localhost:1234/v1`) and mixes in a search by meaning when it can; when it cannot, it searches by keywords only. For another model, write `embedding`. What goes before a question and a chunk (`queryPrefix`, `passagePrefix`) is what the documentation of the model says (`"query: "` and `"passage: "` for e5, as below for Nomic, `""` for a model that needs none):

   ```json
   {
     "roots": ["D:\\MarkPortO-out"],
     "embedding": { "url": "http://localhost:11434/v1", "model": "nomic-embed-text", "queryPrefix": "search_query: ", "passagePrefix": "search_document: " }
   }
   ```

   A cloud embedding API (Voyage AI, OpenAI and so on) and a rerank model can be used too; the text of the chunks is then sent out of the computer. See "A cloud API" in `tools\markporto-rag\README.md`.

4. Register it in the tool. For Claude Code (use the folder where you unpacked the ZIP):

   ```
   claude mcp add --scope user markporto-rag -- uv run --no-project --with mcp --with numpy python C:\MarkPortO\tools\markporto-rag\server.py
   ```

   Other tools, and every item of the configuration, are in [tools/markporto-rag/README.md](../tools/markporto-rag/README.md).
5. Ask the tool, for example "Find in the design documents how an interrupted conversion is resumed, with the pages." It searches with `search_docs`, reads the chunks around with `read_chunk` when it needs to, and answers with the pages; the page images (`pageImages`) show the original.

- The documents do not leave the computer (only when the embedding API is a cloud service, the text of the chunks is sent to it).
- A new or repeated conversion is read again at the next search. Old output of the same document in the same folders gives the same text more than once.
- **Documents kept in a folder for each version**: with every version searched, the versions of the same section fill the top results, and an answer can come from an old version or mix them. Search only the output of the latest version (`roots`), or narrow `search_docs` to a version with `path` (for example `"v1.2"`; ask "in the design documents of v1.2, ..."). Convert with "Make the folder" (the default) so that the versions can be told apart. To see what changed between versions, compare their Markdown with WinMerge or a similar tool.
