# DevStrider — Word macro

The macro each profile's `.docm` must contain. DevStrider calls it over COM with Word invisible:

```
Application.Run "UpdateResumeAndSwitchOriginal", <resume text>
```

**One argument, no clipboard.** The old version read the Windows clipboard, which meant every bid
silently overwrote whatever you had copied — unacceptable when the whole point is that you keep
working in a job application while the resume is produced behind you. Passing a COM argument also
fixes a silent corruption: `CF_TEXT` is ANSI, so ChatGPT's em-dashes and smart quotes arrived as
`?`. A COM `BSTR` is Unicode end to end.

---

## The contract

| DevStrider does | Your macro must |
|---|---|
| Opens the profile's `.docm` invisibly | — |
| Calls the macro with the resume text | Accept **one `String` parameter** |
| Treats a clean return as success | End with `ActiveDocument.Close` |

`Application.Run` is a synchronous COM call, so the macro returning **is** the success signal.
Failure is reported by the macro's error handler writing to `%TEMP%\devstrider_macro_error.log`,
which DevStrider reads immediately afterwards — that log is the only thing that distinguishes a
failed run from a good one.

> **Upgrading from the `Application.Quit` version?** It still works — DevStrider notices Word went
> away and relaunches for the next bid. You just lose the warm instance, which is most of the
> speed-up. The one-line change is in step 6 of the macro below.

The resume text arrives with the trailing fast-feed line already stripped (DevStrider parses that
itself for the bid), but with `[FolderName]:` and every `[Section]:` label intact.

---

## Configuring a profile

Everything that differs between profiles lives in one block at the top of the module. Nothing
below that block should ever need editing per profile, which is the point: a fix made once can be
pasted into every template without re-entering anyone's paths.

| Constant | What it is |
|---|---|
| `OUTPUT_ROOT` | Folder this profile's resumes are written under. Created if missing, parents and all. **No trailing backslash.** |
| `FILE_BASE` | Base name of the two files produced, without extension — what a recruiter sees on the attachment |
| `SECTION_COUNT` | How many `[Subtitle N]` / `[Experience N]` pairs this template has bookmarks for |
| `FALLBACK_FOLDER` | Folder name used when the reply carries no `[FolderName]:` line |
| `EXPORT_PDF` | `False` saves only the `.docx` and skips the PDF, which is roughly half the run time |

`SECTION_COUNT` is the one that isn't obvious. It must match the bookmarks actually in the
document: set it to 5 on a three-role template and the macro looks for `bmSubtitle4` that isn't
there (harmless — that section is skipped); set it to 3 on a five-role template and roles 4 and 5
are **silently dropped** from the resume. Count the `bmExperience…` bookmarks and use that number.

---

## The macro

```vba
'========================================================================
' DevStrider - resume builder
'
' Called by DevStrider over COM:  Application.Run "UpdateResumeAndSwitchOriginal", <text>
' No clipboard access: the resume text arrives as the parameter.
'========================================================================

'------------------------------------------------------------------------
' CONFIG - the only part of this module that differs between profiles.
'------------------------------------------------------------------------

' Where this profile's resumes are written. No trailing backslash.
Private Const OUTPUT_ROOT As String = "C:\Users\Fernando\Music\bid"

' Base name of the two files produced, without extension. This is what a recruiter sees on the
' attachment, so it should be the person the profile is for -- not the Windows account name.
Private Const FILE_BASE As String = "Fernando"

' How many [Subtitle N] / [Experience N] pairs this template has bookmarks for. Must match the
' document: too low and the extra roles are silently dropped.
Private Const SECTION_COUNT As Long = 3

' Folder name used when the reply carries no [FolderName]: line.
Private Const FALLBACK_FOLDER As String = "Resume"

' False saves only the .docx. The PDF export is roughly half the run time.
Private Const EXPORT_PDF As Boolean = True

'------------------------------------------------------------------------
' Nothing below here is per-profile.
'------------------------------------------------------------------------

' Where the macro was working when it failed. Word is invisible, so the error log is the only
' witness -- and "[76] Path not found" without the path is unactionable.
Private gLastPath As String

'========================
' Main macro
'========================
Sub UpdateResumeAndSwitchOriginal(ByVal ClipText As String)
    Dim folderName As String
    Dim sections As Variant
    Dim bookmarks As Variant
    Dim i As Long

    On Error GoTo ErrHandler

    ' 1. Nothing to place -> stop before touching the document.
    If Trim$(ClipText) = "" Then Exit Sub

    ' 2. Output folder name, from the [FolderName]: line.
    folderName = ExtractFolderNameFromClipboard(ClipText)
    If folderName = "" Then folderName = FALLBACK_FOLDER
    folderName = CleanFolderName(folderName)

    ' 3. Section labels and the bookmarks they fill, positionally paired.
    sections = SectionLabels()
    bookmarks = SectionBookmarks()

    ' 4. Fill the bookmarks.
    For i = LBound(sections) To UBound(sections)
        InsertSection ClipText, sections(i), bookmarks(i), sections
    Next i

    ' 5. Save .docx (+ .pdf) into the new folder.
    SaveResumeAutomatically folderName

    ' 6. Close the document -- NOT the application.
    '
    ' This used to be Application.Quit. DevStrider now keeps one hidden Word alive across bids and
    ' opens it while ChatGPT is still writing, so quitting here threw away the thing that makes the
    ' next bid fast -- and, when Word was already running, closed the user's own documents with
    ' SaveChanges:=wdDoNotSaveChanges.
    '
    ' SaveAs2 above already wrote the output, so ActiveDocument is the saved copy at this point:
    ' closing it without saving discards nothing, and the template on disk stays pristine.
    ActiveDocument.Close SaveChanges:=wdDoNotSaveChanges

    Exit Sub

ErrHandler:
    ' Deliberately does NOT close anything. The log entry is what tells DevStrider the run failed;
    ' staying put also leaves the half-built document available for inspection.
    ' The reason is written to disk because Word is invisible -- a MsgBox would hang forever.
    LogMacroError Err.Number, Err.Description
End Sub

'========================
' Label / bookmark tables
'
' Positionally paired: sections(i) is the label whose text fills bookmarks(i). Generated from
' SECTION_COUNT rather than written out, so a template with a different number of roles needs one
' config change instead of two hand-edited Array() literals that have to stay in lockstep -- and
' a pair that drifted out of step would quietly file the wrong text under the wrong heading.
'========================
Function SectionLabels() As Variant
    Dim out() As String, i As Long
    ReDim out(0 To 2 + SECTION_COUNT * 2)
    out(0) = "[Title]:"
    out(1) = "[Summary]:"
    out(2) = "[Skills]:"
    For i = 1 To SECTION_COUNT
        out(1 + i * 2) = "[Subtitle " & i & "]:"
        out(2 + i * 2) = "[Experience " & i & "]:"
    Next i
    SectionLabels = out
End Function

Function SectionBookmarks() As Variant
    Dim out() As String, i As Long
    ReDim out(0 To 2 + SECTION_COUNT * 2)
    out(0) = "bmTitle"
    out(1) = "bmSummary"
    out(2) = "bmSkills"
    For i = 1 To SECTION_COUNT
        out(1 + i * 2) = "bmSubtitle" & i
        out(2 + i * 2) = "bmExperience" & i
    Next i
    SectionBookmarks = out
End Function

'========================
' Error log — %TEMP%\devstrider_macro_error.log
'========================
Sub LogMacroError(ByVal errNumber As Long, ByVal errDescription As String)
    Dim f As Integer, path As String
    On Error Resume Next          ' logging must never mask the original failure
    path = Environ$("TEMP") & "\devstrider_macro_error.log"
    f = FreeFile
    Open path For Append As #f
    Print #f, Format$(Now, "yyyy-mm-dd hh:nn:ss") & "  [" & errNumber & "] " & errDescription & _
              "  |  path=" & gLastPath
    Close #f
End Sub

'========================
' Folder-name helpers
'========================
Function ExtractFolderNameFromClipboard(ByVal fullText As String) As String
    Dim startPos As Long, nextPos As Long
    Dim folderText As String

    startPos = InStr(fullText, "[FolderName]:")
    If startPos = 0 Then
        ExtractFolderNameFromClipboard = ""
        Exit Function
    End If

    startPos = startPos + Len("[FolderName]:")
    nextPos = InStr(startPos, fullText, "[")
    If nextPos = 0 Then nextPos = Len(fullText) + 1

    folderText = Mid(fullText, startPos, nextPos - startPos)
    ExtractFolderNameFromClipboard = Trim(folderText)
End Function

Function CleanFolderName(name As String) As String
    Dim i As Long, c As String, tempName As String
    tempName = ""
    For i = 1 To Len(name)
        c = Mid(name, i, 1)
        If Asc(c) >= 32 And Asc(c) <= 126 Then tempName = tempName & c
    Next i
    tempName = SanitizeFolderName(tempName)
    CleanFolderName = Trim(tempName)
End Function

Function SanitizeFolderName(name As String) As String
    Dim illegalChars As String, i As Long
    illegalChars = "\/:*?""<>|"
    SanitizeFolderName = name
    For i = 1 To Len(illegalChars)
        SanitizeFolderName = Replace(SanitizeFolderName, Mid(illegalChars, i, 1), "-")
    Next i
End Function

'========================
' Bookmark filling
'========================
Sub InsertSection(ByVal fullText As String, ByVal sectionLabel As String, ByVal bookmarkName As String, ByVal allLabels As Variant)
    Dim startPos As Long, nextPos As Long, pos As Long, folderPos As Long
    Dim label As Variant
    Dim sectionText As String, lines() As String, cleanText As String, i As Long
    Dim rng As Range

    startPos = InStr(fullText, sectionLabel)
    If startPos = 0 Then Exit Sub
    startPos = startPos + Len(sectionLabel)

    ' This section runs until whichever label comes next.
    nextPos = Len(fullText) + 1
    For Each label In allLabels
        pos = InStr(startPos, fullText, label)
        If pos > 0 And pos < nextPos Then nextPos = pos
    Next label

    folderPos = InStr(startPos, fullText, "[FolderName]:")
    If folderPos > 0 And folderPos < nextPos Then nextPos = folderPos

    sectionText = Mid(fullText, startPos, nextPos - startPos)

    ' Collapse blank lines, trim each remaining one.
    sectionText = Replace(sectionText, vbCrLf, vbLf)
    lines = Split(sectionText, vbLf)
    cleanText = ""
    For i = LBound(lines) To UBound(lines)
        If Trim(lines(i)) <> "" Then
            If cleanText <> "" Then cleanText = cleanText & vbCrLf
            cleanText = cleanText & Trim(lines(i))
        End If
    Next i

    If ActiveDocument.bookmarks.Exists(bookmarkName) Then
        ' NOTE: assigning to a bookmark's range deletes the bookmark. Harmless here -- each one
        ' is written once and the result is saved to a NEW file, so the template stays pristine
        ' and every run starts from it again.
        Set rng = ActiveDocument.bookmarks(bookmarkName).Range
        rng.Text = cleanText

        ' **bold** -> Word bold
        With rng.Find
            .Text = "\*\*(*)\*\*"
            .Replacement.Text = "\1"
            .Replacement.Font.Bold = True
            .Forward = True
            .Wrap = wdFindStop
            .Format = True
            .MatchWildcards = True
        End With
        rng.Find.Execute Replace:=wdReplaceAll
    End If
End Sub

'========================
' Save .docx + .pdf
'========================
Sub SaveResumeAutomatically(folderName As String)
    Dim fullPath As String, docPath As String, pdfPath As String

    ' A blank root would resolve to "\<folder>" -- the drive root -- and either fail with a
    ' permission error or, worse, succeed somewhere nobody would look. Say so instead.
    If Trim$(OUTPUT_ROOT) = "" Then
        Err.Raise 5, , "OUTPUT_ROOT is empty -- set it in the config block at the top of the module."
    End If

    fullPath = OUTPUT_ROOT & "\" & folderName
    gLastPath = fullPath
    EnsureFolder fullPath

    docPath = fullPath & "\" & FILE_BASE & ".docx"
    pdfPath = fullPath & "\" & FILE_BASE & ".pdf"

    ActiveDocument.SaveAs2 FileName:=docPath, FileFormat:=wdFormatXMLDocument

    If EXPORT_PDF Then
        ActiveDocument.ExportAsFixedFormat _
            OutputFileName:=pdfPath, _
            ExportFormat:=wdExportFormatPDF, _
            OpenAfterExport:=False, _
            OptimizeFor:=wdExportOptimizeForPrint, _
            Range:=wdExportAllDocument, _
            Item:=wdExportDocumentContent, _
            IncludeDocProps:=True, _
            KeepIRM:=True, _
            CreateBookmarks:=wdExportCreateNoBookmarks, _
            DocStructureTags:=True, _
            BitmapMissingFonts:=True, _
            UseISO19005_1:=False
    End If
End Sub

'========================
' Create a folder and every missing parent
'
' VBA's MkDir creates ONE level. The old code called it on OUTPUT_ROOT\folderName, which works only
' if OUTPUT_ROOT already exists -- so the very first run on a machine died with error 76 no matter
' how correct the path was, and creating the leaf by hand fixed only that one bid.
'========================
Sub EnsureFolder(ByVal folderPath As String)
    Dim fso As Object, parent As String
    Set fso = CreateObject("Scripting.FileSystemObject")
    If fso.FolderExists(folderPath) Then Exit Sub

    ' Recursion bottoms out at a drive root, whose parent is "".
    parent = fso.GetParentFolderName(folderPath)
    If parent <> "" Then EnsureFolder parent

    If Not fso.FolderExists(folderPath) Then fso.CreateFolder folderPath
End Sub
```

---

## Installing it

### Into a new template

1. Open the `.docm` → **Alt+F11**
2. Replace the module contents with the block above
3. Edit the five constants in the config block for that profile
4. Save as **macro-enabled** (`.docm`), close Word

### Into a template that already works

Don't paste the whole module over a working one — it may hold per-profile logic this file doesn't
know about, and its VBA is compressed inside the `.docm` where you can't diff it. Instead:

1. Paste the **config block** in above the existing code and set the five constants
2. In the main `Sub`, replace the two `Array(…)` literals with `SectionLabels()` and
   `SectionBookmarks()`, and paste in those two `Function`s
3. In `SaveResumeAutomatically`, replace the hardcoded `basePath` and the two filenames with
   `OUTPUT_ROOT` and `FILE_BASE`
4. If it still ends in `Application.Quit`, change that to `ActiveDocument.Close`

> **The macro will no longer appear in Alt+F8.** Word hides Subs that take parameters. That's
> expected — it's driven by DevStrider, not by hand.

### Required bookmarks

`bmTitle`, `bmSummary`, `bmSkills`, then a `bmSubtitle`/`bmExperience` pair per role up to
`SECTION_COUNT`. A missing bookmark isn't an error — that section just comes back blank.

```
bmTitle   bmSummary   bmSkills
bmSubtitle1   bmExperience1
bmSubtitle2   bmExperience2
bmSubtitle3   bmExperience3       ' and so on, to SECTION_COUNT
```

Any other bookmark in the document is left untouched.

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| Activity: `Macro reported: …` | The macro's error handler ran — the message is verbatim from `%TEMP%\devstrider_macro_error.log` |
| Activity: `Macro call failed: …` | Word never entered the macro: no `Sub` by that name, or it has no single `String` parameter |
| Activity: macro timed out after 90s | The macro is blocking — most often a dialog Word is waiting on. DevStrider closes its Word and recovers on the next bid |
| Activity says success, no file anywhere | The reply reached the macro empty, so it exited at step 1. Success is inferred from a clean return, not from a file appearing |
| Log says `OUTPUT_ROOT is empty` | The config block was pasted but not filled in |
| Log says `[76] Path not found` | `OUTPUT_ROOT` points somewhere that can't be created — a bad drive letter or a permission failure. `EnsureFolder` handles merely-missing folders |
| Last one or two roles missing from the resume | `SECTION_COUNT` is lower than the number of `bmExperience…` bookmarks in the document |
| Document comes back blank | ChatGPT's reply had no `[Section]:` labels — press **Insert default** on the Profiles tab to get a prompt that emits them |
| Files land in a folder named `Resume` | No `[FolderName]:` line in the reply, so `FALLBACK_FOLDER` was used |
| Bid recorded with no company/role | The reply's last line wasn't the bare `UID, Company, Role, …` line |
| Word visible / stealing focus | Something other than DevStrider launched it — the COM path sets `Visible = False` |

---

## Optional: let DevStrider supply the config

The config block makes every template's *code* identical, but the constants still live inside each
`.docm`, so changing an output root means opening Word. Taking two more parameters would move that
into the Profiles tab:

```vba
Sub UpdateResumeAndSwitchOriginal(ByVal ClipText As String, _
                                  ByVal OutputRoot As String, _
                                  ByVal FileBase As String)
```

`SaveResumeAutomatically` would then take them through instead of reading `OUTPUT_ROOT` and
`FILE_BASE`. Requires a matching change in `WordMacroService` to pass them, and two new fields on
the profile. Say the word if you want it.
