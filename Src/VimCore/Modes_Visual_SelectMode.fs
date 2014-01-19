﻿namespace Vim.Modes.Visual
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Text.Editor
open Vim
open Vim.Modes

type internal SelectMode
    (
        _vimBufferData : IVimBufferData,
        _visualKind : VisualKind,
        _commonOperations : ICommonOperations,
        _undoRedoOperations : IUndoRedoOperations,
        _selectionTracker : ISelectionTracker
    ) = 

    let _globalSettings = _vimBufferData.LocalSettings.GlobalSettings
    let _vimTextBuffer = _vimBufferData.VimTextBuffer
    let _textBuffer = _vimBufferData.TextBuffer
    let _textView = _vimBufferData.TextView
    let _modeKind = 
        match _visualKind with
        | VisualKind.Character -> ModeKind.SelectCharacter
        | VisualKind.Line -> ModeKind.SelectLine
        | VisualKind.Block -> ModeKind.SelectBlock

    /// A 'special key' is defined in :help keymodel as any of the following keys.  Depending
    /// on the value of the keymodel setting they can affect the selection
    static let GetCaretMovement (keyInput : KeyInput) =
        if not (Util.IsFlagSet keyInput.KeyModifiers KeyModifiers.Control) then
            match keyInput.Key with
            | VimKey.Up -> Some CaretMovement.Up
            | VimKey.Right -> Some CaretMovement.Right
            | VimKey.Down -> Some CaretMovement.Down
            | VimKey.Left -> Some CaretMovement.Left
            | VimKey.Home -> Some CaretMovement.Home
            | VimKey.End -> Some CaretMovement.End
            | VimKey.PageUp -> Some CaretMovement.PageUp
            | VimKey.PageDown -> Some CaretMovement.PageDown
            | _ -> None
        else
            match keyInput.Key with
            | VimKey.Up -> Some CaretMovement.ControlUp
            | VimKey.Right -> Some CaretMovement.ControlRight
            | VimKey.Down -> Some CaretMovement.ControlDown
            | VimKey.Left -> Some CaretMovement.ControlLeft
            | VimKey.Home -> Some CaretMovement.ControlHome
            | VimKey.End -> Some CaretMovement.ControlEnd
            | _ -> None

    member x.CaretPoint = TextViewUtil.GetCaretPoint _textView

    member x.CaretVirtualPoint = TextViewUtil.GetCaretVirtualPoint _textView

    member x.CaretLine = TextViewUtil.GetCaretLine _textView

    member x.CurrentSnapshot = _textView.TextSnapshot

    member x.ShouldStopSelection (keyInput : KeyInput) =
        let hasShift = Util.IsFlagSet keyInput.KeyModifiers KeyModifiers.Shift
        let hasStopSelection = Util.IsFlagSet _globalSettings.KeyModelOptions KeyModelOptions.StopSelection
        not hasShift && hasStopSelection

    member x.ProcessCaretMovement caretMovement keyInput =
        _commonOperations.MoveCaretWithArrow caretMovement |> ignore

        if x.ShouldStopSelection keyInput then
            ProcessResult.Handled ModeSwitch.SwitchPreviousMode
        else
            // The caret moved so we need to update the selection 
            _selectionTracker.UpdateSelection()
            ProcessResult.Handled ModeSwitch.NoSwitch

    /// The user hit an input key.  Need to replace the current selection with the given 
    /// text and put the caret just after the insert.  This needs to be a single undo 
    /// transaction 
    member x.ProcessInput text = 

        // During undo we don't want the currently selected text to be reselected as that
        // would put the editor back into select mode.  Clear the selection now so that
        // it's not recorderd in the undo transaction and move the caret to the selection
        // start
        let span = _textView.Selection.StreamSelectionSpan.SnapshotSpan
        _textView.Selection.Clear()
        TextViewUtil.MoveCaretToPoint _textView span.Start

        _undoRedoOperations.EditWithUndoTransaction "Replace" (fun () -> 

            use edit = _textBuffer.CreateEdit()

            // First step is to replace the deleted text with the new one
            edit.Delete(span.Span) |> ignore
            edit.Insert(span.End.Position, text) |> ignore

            // Now move the caret past the insert point in the new snapshot. We don't need to 
            // add one here (or even the length of the insert text).  The insert occurred at
            // the exact point we are tracking and we chose PointTrackingMode.Positive so this 
            // will push the point past the insert
            let snapshot = edit.Apply()
            match TrackingPointUtil.GetPointInSnapshot span.End PointTrackingMode.Positive snapshot with
            | None -> ()
            | Some point -> TextViewUtil.MoveCaretToPoint _textView point)

        ProcessResult.Handled (ModeSwitch.SwitchMode ModeKind.Insert)

    /// Handles Ctrl+C/Ctrl+V/Ctrl+X ala $VIMRUNTIME/mswin.vim
    ///
    /// TODO: This should be turned into a mode map
    member x.Process keyInput = 
        let processResult = 
            if keyInput = KeyInputUtil.EscapeKey then
                x.CheckCaretAndSwitchPreviousMode
            elif keyInput = KeyInputUtil.EnterKey then
                let caretPoint = TextViewUtil.GetCaretPoint _textView
                let text = _commonOperations.GetNewLineText caretPoint
                x.ProcessInput text
            elif keyInput.Key = VimKey.Delete || keyInput.Key = VimKey.Back then
                x.ProcessInput "" |> ignore
                x.CheckCaretAndSwitchPreviousMode
            elif keyInput = (KeyInputUtil.CharWithControlToKeyInput 'x') then
                _commonOperations.EditorOperations.CutSelection() |> ignore
                x.CheckCaretAndSwitchPreviousMode
            elif keyInput = KeyInputUtil.CharWithControlToKeyInput 'c' then
                _commonOperations.EditorOperations.CopySelection() |> ignore
                ProcessResult.Handled ModeSwitch.NoSwitch
            elif keyInput = KeyInputUtil.CharWithControlToKeyInput 'v' then
                _commonOperations.EditorOperations.Paste() |> ignore
                ProcessResult.Handled ModeSwitch.NoSwitch
            else
                match GetCaretMovement keyInput with
                | Some caretMovement -> x.ProcessCaretMovement caretMovement keyInput
                | None -> 
                    // TODO: The shift modifier should be removed and the
                    // resulting key should be processed as an ordinary motion.
                    // For example, both Ctrl+F and Ctrl+Shift+F should page
                    // forward.  In any case, we don't want to process it
                    // as text input
                    if Option.isSome keyInput.RawChar && not (CharUtil.IsControl keyInput.Char) then
                        x.ProcessInput (StringUtil.ofChar keyInput.Char)
                    elif x.ShouldStopSelection keyInput then
                        x.CheckCaretAndSwitchPreviousMode
                    else
                        ProcessResult.Handled ModeSwitch.NoSwitch

        if processResult.IsAnySwitch then
            _textView.Selection.Clear()
            _textView.Selection.Mode <- TextSelectionMode.Stream

        processResult

    member x.CheckCaretAndSwitchPreviousMode =
        _commonOperations.EnsureAtCaret ViewFlags.VirtualEdit
        ProcessResult.Handled ModeSwitch.SwitchPreviousMode

    member x.OnEnter modeArgument = 
        match modeArgument with
        | ModeArgument.InitialVisualSelection (visualSelection, caretPoint) ->

            if visualSelection.VisualKind = _visualKind then
                visualSelection.Select _textView
                let visualCaretPoint = visualSelection.GetCaretPoint _globalSettings.SelectionKind
                TextViewUtil.MoveCaretToPointRaw _textView visualCaretPoint MoveCaretFlags.EnsureOnScreen
        | _ -> ()

        _selectionTracker.Start()
    member x.OnLeave() = _selectionTracker.Stop()
    member x.OnClose() = ()

    member x.SyncSelection() =
        if _selectionTracker.IsRunning then
            _selectionTracker.Stop()
            _selectionTracker.Start()

    interface ISelectMode with
        member x.SyncSelection() = x.SyncSelection()

    interface IMode with
        member x.VimTextBuffer = _vimTextBuffer
        member x.CommandNames = Seq.empty
        member x.ModeKind = _modeKind
        member x.CanProcess _ = true
        member x.Process keyInput =  x.Process keyInput
        member x.OnEnter modeArgument = x.OnEnter modeArgument
        member x.OnLeave () = x.OnLeave()
        member x.OnClose() = x.OnClose()

