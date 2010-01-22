﻿#light

namespace Vim.Modes.Command
open Vim
open Vim.Modes
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Operations
open System.Windows.Input
open System.Text.RegularExpressions
open Vim.RegexUtil

type internal DefaultOperations
    (
        _textView : ITextView,
        _operations : IEditorOperations, 
        _host : IVimHost ) =
    inherit CommonOperations(_textView, _operations) 

    member private x.CommonImpl = x :> ICommonOperations

    /// Get the Line spans in the specified range
    member x.GetSpansInRange (range:SnapshotSpan) =
        let startLine = range.Start.GetContainingLine()
        let endLine = range.End.GetContainingLine()
        if startLine.LineNumber = endLine.LineNumber then  Seq.singleton range
        else
            let tss = startLine.Snapshot
            seq {
                yield SnapshotSpan(range.Start, startLine.EndIncludingLineBreak)
                for i = startLine.LineNumber + 1 to endLine.LineNumber - 1 do
                    yield tss.GetLineFromLineNumber(i).ExtentIncludingLineBreak
                yield SnapshotSpan(endLine.Start, range.End)
            }

    interface IOperations with
        member x.EditFile fileName = _host.OpenFile fileName

        /// Implement the :pu[t] command
        member x.Put (text:string) (line:ITextSnapshotLine) isAfter =

            // Force a newline at the end to be consistent with the implemented 
            // behavior in various VIM implementations.  This isn't called out in 
            // the documentation though
            let text = 
                if text.EndsWith(System.Environment.NewLine) then text
                else text + System.Environment.NewLine

            let point = line.Start
            let span =
                if isAfter then x.CommonImpl.PasteAfter point text OperationKind.LineWise
                else x.CommonImpl.PasteBefore point text

            // Move the cursor to the first non-blank character on the last line
            let line = span.End.Subtract(1).GetContainingLine()
            let rec inner (cur:SnapshotPoint) = 
                if cur = line.End || not (System.Char.IsWhiteSpace(cur.GetChar())) then
                    cur
                else
                    inner (cur.Add(1))
            let point = inner line.Start
            _textView.Caret.MoveTo(point) |> ignore

        member x.Substitute pattern replace (range:SnapshotSpan) flags = 

            /// Actually do the replace with the given regex
            let doReplace (regex:Regex) = 
                use edit = _textView.TextBuffer.CreateEdit()

                let replaceOne (span:SnapshotSpan) (c:Capture) = 
                    let newText = regex.Replace(c.Value, replace, 1)
                    let offset = span.Start.Position
                    edit.Replace(Span(c.Index+offset, c.Length), newText) |> ignore
                let getMatches (span:SnapshotSpan) = 
                    if Utils.IsFlagSet flags SubstituteFlags.ReplaceAll then
                        regex.Matches(span.GetText()) |> Seq.cast<Match>
                    else
                        regex.Match(span.GetText()) |> Seq.singleton
                let matches = 
                    x.GetSpansInRange range
                    |> Seq.map (fun span -> getMatches span |> Seq.map (fun m -> (m,span)) )
                    |> Seq.concat 
                    |> Seq.filter (fun (m,_) -> m.Success)

                if not (Utils.IsFlagSet flags SubstituteFlags.ReportOnly) then
                    // Actually do the edits
                    matches |> Seq.iter (fun (m,span) -> replaceOne span m)

                let updateReplaceCount () = 
                    // Update the status
                    let replaceCount = matches |> Seq.length
                    let lineCount = 
                        matches 
                        |> Seq.map (fun (_,s) -> s.Start.GetContainingLine().LineNumber)
                        |> Seq.distinct
                        |> Seq.length
                    if replaceCount > 1 then
                        _host.UpdateStatus (Resources.CommandMode_SubstituteComplete replaceCount lineCount)

                if edit.HasEffectiveChanges then
                    edit.Apply() |> ignore                                
                    updateReplaceCount()
                elif Utils.IsFlagSet flags SubstituteFlags.ReportOnly then
                    edit.Cancel()
                    updateReplaceCount()
                else 
                    edit.Cancel()
                    _host.UpdateStatus (Resources.CommandMode_PatternNotFound pattern)

            let options = 
                if Utils.IsFlagSet flags SubstituteFlags.IgnoreCase then
                    RegexOptions.IgnoreCase
                else    
                    RegexOptions.None
            let options = options ||| RegexOptions.Compiled
            match Utils.TryCreateRegex pattern options with
            | None -> _host.UpdateStatus (Resources.CommandMode_PatternNotFound pattern)
            | Some (regex) -> doReplace regex