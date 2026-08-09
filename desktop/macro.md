# DevStrider — Word macro

The macro each profile's `.docm` must contain. DevStrider calls it over COM with Word invisible:

```
$word.Run("UpdateResumeAndSwitchOriginal", <resume text>)
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
| Waits for Word to close | End with `Application.Quit` |

Word closing **is** the success signal. A macro that returns without quitting is reported as a
failure after 90 seconds — which is the correct outcome for a failed run, just a slow one.

The resume text arrives with the trailing fast-feed line already stripped (DevStrider parses that
itself for the bid), but with `[FolderName]:` and every `[Section]:` label intact.

---

## The macro

Replace the entire module with this. Everything below the main `Sub` is unchanged from what you
already had.

```vba
'========================
' DevStrider — resume builder
'
' Called by DevStrider over COM:  Application.Run "UpdateResumeAndSwitchOriginal", <text>
' No clipboard access: the resume text arrives as the parameter.
'========================

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
    If folderName = "" Then folderName = "Resume"
    folderName = CleanFolderName(folderName)

    ' 3. Section labels and the bookmarks they fill, positionally paired.
    sections = Array("[Title]:", "[Summary]:", "[Skills]:", "[Subtitle 1]:", "[Experience 1]:", _
                     "[Subtitle 2]:", "[Experience 2]:", "[Subtitle 3]:", "[Experience 3]:")

    bookmarks = Array("bmTitle", "bmSummary", "bmSkills", "bmSubtitle1", "bmExperience1", _
                      "bmSubtitle2", "bmExperience2", "bmSubtitle3", "bmExperience3")

    ' 4. Fill the bookmarks.
    For i = LBound(sections) To UBound(sections)
        InsertSection ClipText, sections(i), bookmarks(i), sections
    Next i

    ' 5. Save .docx + .pdf into the new folder.
    SaveResumeAutomatically folderName

    ' 6. Quit. DevStrider treats Word closing as "the run finished".
    Application.Quit SaveChanges:=wdDoNotSaveChanges

    Exit Sub

ErrHandler:
    ' Deliberately does NOT quit. Word staying open is what tells DevStrider the run failed;
    ' quitting here would report success for a document that was never produced.
    ' The reason is written to disk because Word is invisible -- a MsgBox would hang forever.
    LogMacroError Err.Number, Err.Description
End Sub

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
    Dim basePath As String, fullPath As String, docPath As String, pdfPath As String

    ' Per-profile: each template hardcodes its own owner's output root and file name.
    basePath = "C:\Users\lenovo\Music\bid"
    fullPath = basePath & "\" & folderName

    gLastPath = fullPath
    EnsureFolder fullPath

    ' The account name only ever belonged in basePath. This is the resume's filename -- it should
    ' be the person the profile is for, which is what a recruiter sees on the attachment.
    docPath = fullPath & "\Fernando.docx"
    pdfPath = fullPath & "\Fernando.pdf"

    ActiveDocument.SaveAs2 FileName:=docPath, FileFormat:=wdFormatXMLDocument

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
End Sub

'========================
' Create a folder and every missing parent
'
' VBA's MkDir creates ONE level. The old code called it on basePath\folderName, which works only
' if basePath already exists -- so the very first run on a machine died with error 76 no matter
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

## What changed from the old version

| | |
|---|---|
| **Removed** | All seven `Declare PtrSafe` lines, `Const CF_TEXT`, and `GetClipboardText` |
| **Changed** | `Sub UpdateResumeAndSwitchOriginal()` → `(ByVal ClipText As String)` |
| **Removed** | `ClipText = GetClipboardText()` — it arrives as the parameter |
| **Added** | `LogMacroError` — the old handler swallowed every error silently, leaving no way to tell a failed run from a wrong one |
| **Added** | `EnsureFolder` — `MkDir` creates one level, so the output root was never created and every first run failed with error 76 |
| **Added** | `gLastPath` in the log line — `[76] Path not found` didn't say *which* path, which made the failure unactionable |
| **Tidied** | Dropped unused `savedDocPath` / `originalPath` / `newWordApp`; hoisted `pos` out of the loop |

Everything else — section parsing, bookmarks, `**bold**`, `SaveAs2`, PDF export, `Application.Quit`
— is byte-for-byte what you had.

---

## Installing it

Per profile, once:

1. Open the `.docm` → **Alt+F11**
2. Replace the module contents with the block above
3. Adjust `basePath` and the two file names in `SaveResumeAutomatically` for that profile's owner
4. Save as **macro-enabled** (`.docm`), close Word

> **The macro will no longer appear in Alt+F8.** Word hides Subs that take parameters. That's
> expected — it's driven by DevStrider, not by hand.

### Required bookmarks

The document must contain all nine, or those sections come back blank:

```
bmTitle   bmSummary   bmSkills
bmSubtitle1   bmExperience1
bmSubtitle2   bmExperience2
bmSubtitle3   bmExperience3
```

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| Activity: macro failed after ~90s | The `Sub` still has no `String` parameter, or it errored — check `%TEMP%\devstrider_macro_error.log` |
| Log says `[76] Path not found` | `basePath` points somewhere that doesn't exist. The `path=` on the log line is the folder it tried; `EnsureFolder` creates it now, so this should only mean a bad drive letter or a permission failure |
| Document comes back blank | ChatGPT's reply had no `[Section]:` labels — press **Insert default** on the Profiles tab to get a prompt that emits them |
| Folder named `Resume` | No `[FolderName]:` line in the reply |
| Bid recorded with no company/role | The reply's last line wasn't the bare `UID, Company, Role, …` line |
| Word visible / stealing focus | Something other than DevStrider launched it — the COM path sets `Visible = False` |

---

## Optional: one macro for every profile

Right now each `.docm` hardcodes its owner's path and file name, so the macro text differs per
profile — change the logic once and you edit every file. Taking two more parameters makes the
macro identical everywhere, with DevStrider supplying the difference:

```vba
Sub UpdateResumeAndSwitchOriginal(ByVal ClipText As String, _
                                  ByVal OutputRoot As String, _
                                  ByVal FileBase As String)
...
Sub SaveResumeAutomatically(folderName As String, OutputRoot As String, FileBase As String)
    basePath = OutputRoot                          ' was "C:\Users\Fernando\Music\bid"
    ...
    docPath = fullPath & "\" & FileBase & ".docx"  ' was "\Fernando.docx"
    pdfPath = fullPath & "\" & FileBase & ".pdf"
```

Requires a matching change in `WordMacroService` to pass them. Say the word if you want it.
