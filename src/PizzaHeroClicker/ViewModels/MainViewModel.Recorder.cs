using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;

namespace PizzaHeroClicker.ViewModels;

// Record and playback: capture real input, then convert it into the editable action list.
public partial class MainViewModel
{
    public const string HkRecord = "record";

    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _recordStatus = "";

    public string RecordButtonText
    {
        get
        {
            string key = Profile.Hotkeys.Record.IsEmpty ? "" : $"  [{Profile.Hotkeys.Record}]";
            return (IsRecording ? "■ STOP RECORDING" : "● RECORD") + key;
        }
    }

    partial void OnIsRecordingChanged(bool value) => OnPropertyChanged(nameof(RecordButtonText));

    [RelayCommand]
    private void ToggleRecording()
    {
        if (IsRecording) FinishRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        if (_s.Engine.IsActive)
        {
            Footer = "Stop the current run before recording.";
            return;
        }
        if (!_s.Recorder.Start(Settings.RecordMouseMoves))
        {
            Footer = "Recording could not start. See the log for details.";
            return;
        }
        IsRecording = true;
        Footer = Profile.Hotkeys.Record.IsEmpty
            ? "Recording. Press STOP RECORDING when you are done."
            : $"Recording. Press {Profile.Hotkeys.Record} when you are done.";
    }

    private void FinishRecording()
    {
        var events = _s.Recorder.Stop();
        IsRecording = false;
        RecordStatus = "";

        var options = new RecordingOptions(Settings.TrimDelays, Settings.MaxDelayMs)
        {
            // The app's own hotkeys (including the one that just stopped the recording) are not part of the macro.
            IgnoredCombos = BuildHotkeyRequests().Where(r => !r.Combo.IsEmpty).Select(r => r.Combo).ToHashSet(),
        };
        var actions = RecordingConverter.Convert(events, options);
        if (actions.Count == 0)
        {
            Footer = "Nothing was recorded.";
            return;
        }

        if (Profile.Window is { Enabled: true, RelativeCoordinates: true })
        {
            if (CoordinateOrigin() is var (ox, oy))
            {
                foreach (var action in actions) action.Offset(-ox, -oy);
            }
            else
            {
                _s.Dialogs.Inform("Recording", "The target window isn't open, so the recording can't be stored in window-relative coordinates. It was discarded.");
                return;
            }
        }

        int choice = 0;
        if (Profile.Actions.Count > 0)
        {
            choice = _s.Dialogs.Choose("Recording finished",
                $"Recorded {actions.Count} actions. This profile already has {Profile.Actions.Count}.",
                "Replace the list", "Add to the end", "Discard");
        }
        switch (choice)
        {
            case 0:
                Profile.Actions.Clear();
                break;
            case 1:
                break;
            default:
                Footer = "Recording discarded.";
                return;
        }

        foreach (var action in actions) Profile.Actions.Add(action);
        SelectedAction = null;
        Footer = $"Recorded {actions.Count} actions. Edit them on the Actions tab; set \"When to stop\" to 1 loop to play it once.";
    }

    private void UpdateRecordStatus()
    {
        if (IsRecording) RecordStatus = $"Recording… {_s.Recorder.EventCount:N0} events";
    }
}
