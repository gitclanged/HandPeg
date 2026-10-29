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
/// listen to (the voice clip or the Voice track by default; any track of the main video, any sound clip, a separate
/// recording or the voiceover), the speech model, the language, translation and words to expect. Nothing is changed until
/// Generate is pressed: the choices are then handed back for the main window to apply and run, so one click on Generate
/// makes captions with the defaults.
/// </summary>
public partial class AutoCaptionsDialog : Window
{
    /// <summary>One entry of the Listen To box: a source, or (null) the choice to pick a separate file.</summary>
    private sealed record SourceItem(string Label, CaptionSource? Source);

    private readonly MainViewModel _viewModel;
    private SourceItem? _lastChosen;

    /// <summary>What to listen to, as chosen.</summary>
    public CaptionSource? Source { get; private set; }

    public string Model => ModelBox.SelectedItem as string ?? _viewModel.WhisperModelChoice;
    public string SpokenLanguage => LanguageBox.SelectedItem as string ?? _viewModel.WhisperLanguage;
    public bool Translate => TranslateBox.IsChecked == true;
    public string Prompt => PromptBox.Text.Trim();

    /// <param name="requested">What was asked for (a track of the main video, a clip with sound), or null for what is set.</param>
    public AutoCaptionsDialog(MainViewModel viewModel, object? requested = null)
    {
        InitializeComponent();
        _viewModel = viewModel;

        // Every sound the timeline can make captions from: the main video's tracks, each sound clip's file once, the voiceover.
        var items = new List<SourceItem>();
        if (viewModel.HasSource)
        {
            foreach (var track in viewModel.AudioTracks)
                items.Add(Item(new CaptionSource(CaptionSourceKind.MainFile, viewModel.LocalMediaPath, track.Index)));
        }

        foreach (var clip in viewModel.Layers.Where(l => l is { CarriesSound: true, IsMainVideo: false }).DistinctBy(l => l.ImagePath, StringComparer.OrdinalIgnoreCase))
        {
            if (viewModel.ResolveCaptionSource(clip) is { Kind: CaptionSourceKind.Clip } source)
                items.Add(Item(source));
        }

        if (viewModel.HasVoiceover)
            items.Add(Item(new CaptionSource(CaptionSourceKind.Voiceover, viewModel.VoiceoverPath, 0)));

        // What is set now (or asked for) comes first among equals; a separate file chosen before is offered as it was.
        var current = viewModel.ResolveCaptionSource(requested);
        if (current is { Kind: CaptionSourceKind.External } && current.Path.Length > 0)
            items.Add(Item(current));
        items.Add(new SourceItem("Separate Audio File...", null));

        SourceBox.ItemsSource = items;
        SourceBox.SelectedItem = items.FirstOrDefault(i => i.Source is { } s && s.SameAs(current) && (s.Kind != CaptionSourceKind.MainFile || s.Stream == current!.Stream))
                                 ?? items.FirstOrDefault(i => i.Source is not null);

        ModelBox.ItemsSource = viewModel.WhisperModelChoices;
        ModelBox.SelectedItem = viewModel.WhisperModelChoice;
        LanguageBox.ItemsSource = viewModel.WhisperLanguages;
        LanguageBox.SelectedItem = viewModel.WhisperLanguage;
        TranslateBox.IsChecked = viewModel.WhisperTranslate;
        PromptBox.Text = viewModel.WhisperPrompt;

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

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (SourceBox.SelectedItem is not SourceItem { Source: { } source })
            return;

        if (source.Kind is CaptionSourceKind.External && !File.Exists(source.Path))
        {
            NoteText.Text = $"The file was not found: {source.Path}";
            return;
        }

        Source = source;
        DialogResult = true;
    }
}
