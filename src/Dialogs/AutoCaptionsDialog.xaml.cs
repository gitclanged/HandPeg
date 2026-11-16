using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using Microsoft.Win32;

namespace HandPegApp;

/// <summary>
/// Generate Auto-Captions dialog: source (default voice clip; any video stream, audio clip, separate recording or voiceover), speech
/// model, language, translation and expected words. Nothing changes until Generate, which returns the choices for the main window to
/// apply and run (one click uses defaults). The accelerator (NPU, GPU, CPU) is AppSettings.CaptionAccelerator, shown with its effective
/// backend and persisted when changed.
/// </summary>
public partial class AutoCaptionsDialog : Window
{
    /// <summary>Listen To entry: a source, or null for "choose a separate file".</summary>
    private sealed record SourceItem(string Label, CaptionSource? Source);

    /// <summary>Runs On entry: setting value and label.</summary>
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

    /// <summary>Selected audio source.</summary>
    public CaptionSource? Source { get; private set; }

    public string Model => ModelBox.SelectedItem as string ?? _viewModel.WhisperModelChoice;
    public string SpokenLanguage => LanguageBox.SelectedItem as string ?? _viewModel.WhisperLanguage;
    public bool Translate => TranslateBox.IsChecked == true;
    public string Prompt => PromptBox.Text.Trim();

    /// <param name="requested">Requested source (video/audio track or clip with audio), or null for the current setting.</param>
    public AutoCaptionsDialog(MainViewModel viewModel, object? requested = null)
    {
        InitializeComponent();
        _viewModel = viewModel;

        // From a track/clip menu: exactly its audio streams. Otherwise every captionable audio source on the timeline (each clip
        // file/stream once, stream Sub-Lanes included) plus the voiceover.
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

        // Current/requested source wins ties; a previously chosen separate file is re-offered.
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

    /// <summary>Selecting the separate-file entry opens a picker; the box shows the file or reverts on cancel.</summary>
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

    /// <summary>Resolves the effective accelerator (NPU, GPU, CPU) and reason on a pool thread.</summary>
    private void Accelerator_SelectionChanged(object sender, SelectionChangedEventArgs e) => Faults.Forget(Accelerator_SelectionChangedAsync(sender, e), nameof(Accelerator_SelectionChanged));

    private async Task Accelerator_SelectionChangedAsync(object sender, SelectionChangedEventArgs e)
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

        // Persist an accelerator change (shared with Settings > GPU & NPU Roles).
        if (AcceleratorBox.SelectedValue is string chosen && chosen != AppSettings.Current.CaptionAccelerator)
        {
            AppSettings.Update(settings => settings.CaptionAccelerator = chosen);
        }

        Source = source;
        DialogResult = true;
    }
}
