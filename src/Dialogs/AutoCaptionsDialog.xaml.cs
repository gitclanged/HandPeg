using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using Microsoft.Win32;

namespace HandPegApp;

/// <summary>
/// Generate Auto-Captions (4.4): what the Subtitles tab held for making captions, asked where they are made. What to
/// listen to (the voice clip or the Voice track by default; any audio stream of a video clip, any sound clip, a separate
/// recording or the voiceover), the speech model, the language, translation and words to expect. Nothing is changed until
/// Generate is pressed: the choices are then handed back for the main window to apply and run, so one click on Generate
/// makes captions with the defaults. What it runs on (the NPU, the graphics card, the processor: 5.2-Arm64AndNpu) is the
/// setting's (AppSettings.CaptionAccelerator), shown with what it comes to here, and kept when changed.
/// </summary>
public partial class AutoCaptionsDialog : Window
{
    /// <summary>One entry of the Listen To box: a source, or (null) the choice to pick a separate file.</summary>
    private sealed record SourceItem(string Label, CaptionSource? Source);

    /// <summary>One entry of the Runs On box: the setting's value and its name.</summary>
    private sealed record AcceleratorItem(string Name, string Display);

    private static readonly AcceleratorItem[] Accelerators =
    [
        new(AppSettings.SpeechOnAuto, "Auto"),
        new(AppSettings.SpeechOnNpu, "NPU"),
        new(AppSettings.SpeechOnGpu, "Graphics Card"),
        new(AppSettings.SpeechOnCpu, "Processor"),
    ];

    private readonly MainViewModel _viewModel;
    private SourceItem? _lastChosen;

    /// <summary>What to listen to, as chosen.</summary>
    public CaptionSource? Source { get; private set; }

    public string Model => ModelBox.SelectedItem as string ?? _viewModel.WhisperModelChoice;
    public string SpokenLanguage => LanguageBox.SelectedItem as string ?? _viewModel.WhisperLanguage;
    public bool Translate => TranslateBox.IsChecked == true;
    public string Prompt => PromptBox.Text.Trim();

    /// <param name="requested">What was asked for (a video's or a sound's track, a clip with sound), or null for what is set.</param>
    public AutoCaptionsDialog(MainViewModel viewModel, object? requested = null)
    {
        InitializeComponent();
        _viewModel = viewModel;

        // A track's or a clip's own menu: all of its audio streams, and nothing else (the user's directive). Otherwise every sound the
        // timeline can make captions from (5.2-AuditB: a file's streams are its clips' Sub-Lanes): each clip's file and stream once,
        // the voiceover.
        var items = new List<SourceItem>();
        var scoped = viewModel.CaptionStreamsOf(requested);
        if (scoped is { Count: > 0 })
            items.AddRange(scoped.Select(Item));

        if (scoped is not { Count: > 0 })
        {
            var sounds = viewModel.Layers.Where(l => l.CarriesSound).Select(l => viewModel.ResolveCaptionSource(l)).OfType<CaptionSource>();
            foreach (var source in sounds.Where(s => s.Kind == CaptionSourceKind.Clip).DistinctBy(s => (s.Path.ToUpperInvariant(), s.Stream)))
                items.Add(Item(source));

            if (viewModel.HasVoiceover)
                items.Add(Item(new CaptionSource(CaptionSourceKind.Voiceover, viewModel.VoiceoverPath, 0)));
        }

        // What is set now (or asked for) comes first among equals; a separate file chosen before is offered as it was.
        var current = viewModel.ResolveCaptionSource(requested);
        if (current is { Kind: CaptionSourceKind.External } && current.Path.Length > 0)
            items.Add(Item(current));
        items.Add(new SourceItem("Separate Audio File...", null));

        SourceBox.ItemsSource = items;
        SourceBox.SelectedItem = items.FirstOrDefault(i => i.Source is { } s && s.SameAs(current) && s.Stream == current!.Stream)
                                 ?? items.FirstOrDefault(i => i.Source is { } s && s.SameAs(current))
                                 ?? items.FirstOrDefault(i => i.Source is not null);

        ModelBox.ItemsSource = viewModel.WhisperModelChoices;
        ModelBox.SelectedItem = viewModel.WhisperModelChoice;
        LanguageBox.ItemsSource = viewModel.WhisperLanguages;
        LanguageBox.SelectedItem = viewModel.WhisperLanguage;
        TranslateBox.IsChecked = viewModel.WhisperTranslate;
        PromptBox.Text = viewModel.WhisperPrompt;
        AcceleratorBox.ItemsSource = Accelerators;
        AcceleratorBox.SelectedValue = AppSettings.CaptionAccelerators.Contains(AppSettings.Current.CaptionAccelerator)
            ? AppSettings.Current.CaptionAccelerator : AppSettings.SpeechOnAuto;

        NoteText.Text = viewModel.HasSubtitleCues
            ? "The captions made replace the subtitle track there is now, and what was edited in it."
            : "The caption style, its place and its look are set from the caption row's menu.";
        GenerateButton.IsEnabled = SourceBox.SelectedItem is SourceItem { Source: not null };
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private SourceItem Item(CaptionSource source) => new(_viewModel.DescribeCaptionSource(source), source);

    /// <summary>The separate file entry asks for the file at once; the box then shows it, or goes back to what it showed.</summary>
    private void Source_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceBox.SelectedItem is not SourceItem item)
            return;

        if (item.Source is not null)
        {
            _lastChosen = item;
            GenerateButton.IsEnabled = true;
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select the audio to transcribe",
            Filter = "Audio and video files|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma;*.mp4;*.mkv;*.mov;*.webm|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            var chosen = Item(new CaptionSource(CaptionSourceKind.External, dialog.FileName, 0));
            var items = ((List<SourceItem>)SourceBox.ItemsSource).ToList();
            items.Insert(items.Count - 1, chosen);
            SourceBox.ItemsSource = items;
            SourceBox.SelectedItem = chosen;
            return;
        }

        SourceBox.SelectedItem = _lastChosen;
        GenerateButton.IsEnabled = _lastChosen is not null;
    }

    /// <summary>What the choice comes to here (the NPU, the graphics card, the processor) and why, worked out on a pool thread.</summary>
    private async void Accelerator_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var choice = AcceleratorBox.SelectedValue as string ?? AppSettings.SpeechOnAuto;
        try
        {
            var plan = await Task.Run(() => CaptionGenerator.PlanSpeech(choice));
            if ((AcceleratorBox.SelectedValue as string ?? AppSettings.SpeechOnAuto) == choice)
                AcceleratorText.Text = plan.Why;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("What Auto-Captions run on could not be worked out", ex);
            AcceleratorText.Text = "";
        }
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (SourceBox.SelectedItem is not SourceItem { Source: { } source })
            return;

        if (source.Kind is CaptionSourceKind.External && !File.Exists(source.Path))
        {
            NoteText.Text = $"The file was not found: {source.Path}";
            return;
        }

        // A changed choice of what it runs on is kept (the same setting as Settings' GPU & NPU Roles).
        if (AcceleratorBox.SelectedValue is string chosen && chosen != AppSettings.Current.CaptionAccelerator)
        {
            AppSettings.Current.CaptionAccelerator = chosen;
            try
            {
                AppSettings.Current.SaveAsCurrent();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // It holds for this session.
                AppLog.Write("The Auto-Captions accelerator could not be saved", ex);
            }
        }

        Source = source;
        DialogResult = true;
    }
}
