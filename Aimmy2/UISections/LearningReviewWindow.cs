using Aimmy2.ModelLearning;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShapeRectangle = System.Windows.Shapes.Rectangle;

namespace Aimmy2.Controls;

/// <summary>Local human review; proposals never become labels without an explicit review action.</summary>
internal sealed class LearningReviewWindow : Window
{
    private readonly ModelLearningCoordinator coordinator;
    private readonly string[] classNames;
    private readonly string? requestedModelHash;
    private readonly TextBlock summary = Text("Chargement des images locales…", 15);
    private readonly TextBlock detail = Text("", 13);
    private readonly TextBlock feedback = Text("", 14);
    private readonly ComboBox classPicker = new() { MinWidth = 220, Margin = new Thickness(4), Padding = new Thickness(8) };
    private readonly Canvas canvas = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly Image image = new() { IsHitTestVisible = false, Stretch = Stretch.Fill };
    private readonly List<LearningBox> boxes = new();
    private readonly Dictionary<string, int> reviewedBySession = new(StringComparer.Ordinal);
    private readonly List<UIElement> drawn = new();
    private readonly Button approve;
    private readonly Button negative;
    private readonly Button skip;
    private readonly Button clear;
    private LearningCapture[] queue = Array.Empty<LearningCapture>();
    private int position, reviewedBefore, savedHere, excludedModels;
    private bool busy, closed;
    private Point? dragStart;
    private ShapeRectangle? draft;

    public LearningReviewWindow(ModelLearningCoordinator coordinator, string[] classNames, string? currentModelHash = null)
    {
        this.coordinator = coordinator;
        this.classNames = classNames.Length > 0 ? classNames : new[] { "cible" };
        requestedModelHash = currentModelHash;
        Title = "AutoAimmy — vérifier les images locales";
        Width = 1080; Height = 900; MinWidth = 720; MinHeight = 680;
        Background = Brush("#101426"); Foreground = Brush("#EDF0FF");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new StackPanel();
        top.Children.Add(Text("Vérifier les cibles du modèle", 24, FontWeights.SemiBold));
        top.Children.Add(Text("Vérifiez TOUTES les cibles : retirez les fausses boîtes avec un clic droit et dessinez les cibles oubliées en glissant le bouton gauche. Choisissez la classe avant de dessiner.", 14));
        top.Children.Add(Text("Une forte confiance du modèle ne prouve pas qu’une boîte est correcte. Ne validez que les images que vous pouvez vraiment vérifier. Tout reste sur ce PC.", 13));
        top.Children.Add(summary);
        var tools = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        tools.Children.Add(Text("Classe à dessiner :", 14));
        for (int i = 0; i < this.classNames.Length; i++) classPicker.Items.Add($"{i} — {this.classNames[i]}");
        classPicker.SelectedIndex = 0; tools.Children.Add(classPicker);
        clear = Button("Effacer les boîtes", (_, _) => { boxes.Clear(); DrawBoxes(); }); tools.Children.Add(clear);
        top.Children.Add(tools); top.Children.Add(detail);
        Grid.SetRow(top, 0); layout.Children.Add(top);
        canvas.Children.Add(image);
        var viewbox = new Viewbox { Child = canvas, Stretch = Stretch.Uniform, Margin = new Thickness(0, 10, 0, 10) };
        var frame = new Border { Child = viewbox, Background = Brushes.Black, CornerRadius = new CornerRadius(8) };
        Grid.SetRow(frame, 1); layout.Children.Add(frame);
        canvas.MouseLeftButtonDown += BeginDraw;
        canvas.MouseMove += MoveDraw;
        canvas.MouseLeftButtonUp += EndDraw;
        canvas.MouseRightButtonDown += RemoveBox;
        var bottom = new StackPanel();
        bottom.Children.Add(feedback);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        approve = Button("Toutes les cibles sont correctes — enregistrer", async (_, _) => await SaveAsync(false));
        approve.Background = Brush("#315FAD"); approve.Foreground = Brushes.White;
        negative = Button("Confirmer : aucune cible dans l’image", async (_, _) => await SaveAsync(true));
        skip = Button("Passer sans enregistrer", async (_, _) => { position++; await ShowCurrentAsync(); });
        actions.Children.Add(approve); actions.Children.Add(negative); actions.Children.Add(skip);
        actions.Children.Add(Button("Fermer", (_, _) => Close()));
        bottom.Children.Add(actions);
        bottom.Children.Add(Text("Pour comparer : au moins 20 images vérifiées dans chacune de deux sessions différentes, avec au moins 20 cibles annotées dans la session de validation. Une image sans cible n’est validée que par le bouton dédié.", 12));
        Grid.SetRow(bottom, 2); layout.Children.Add(bottom); Content = layout;
        Loaded += async (_, _) => await LoadQueueAsync();
        Closed += (_, _) => { closed = true; canvas.ReleaseMouseCapture(); };
        SetBusy(true);
    }

    private static SolidColorBrush Brush(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    private static TextBlock Text(string text, double size, FontWeight? weight = null) => new() {
        Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
        Foreground = Brush("#D9DEEF"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
    private static Button Button(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(12, 9, 12, 9),
            Background = Brush("#2A3048"), Foreground = Brushes.White, BorderBrush = Brush("#515976"), FontSize = 13 };
        button.Click += click; return button;
    }

    private async Task LoadQueueAsync()
    {
        try
        {
            string modelPath = Aimmy2.LocalAutomation.LocalAutomationSession.Instance.ModelPath;
            var loaded = await Task.Run(() => {
                string? hash = requestedModelHash;
                if (hash == null && File.Exists(modelPath)) hash = ModelLearningCoordinator.HashFile(modelPath);
                return (Captures: coordinator.GetCaptures(), Hash: hash);
            });
            if (closed) return;
            var relevant = loaded.Captures.Where(c => loaded.Hash == null || c.ModelSha256 == loaded.Hash).ToArray();
            excludedModels = loaded.Captures.Length - relevant.Length;
            foreach (var group in relevant.Where(c => c.ReviewStatus == "HumanReviewed").GroupBy(c => c.SessionId))
                reviewedBySession[group.Key] = group.Count();
            reviewedBefore = reviewedBySession.Values.Sum();
            queue = relevant.Where(c => c.ReviewStatus == "Unreviewed").OrderBy(c => c.CapturedUtc).ThenBy(c => c.Id).ToArray();
            if (loaded.Hash == null) feedback.Text = "Aucun modèle actif : vérifiez soigneusement que chaque nom de classe correspond aux images. Les propositions peuvent venir de modèles différents.";
            await ShowCurrentAsync();
        }
        catch (Exception error) { feedback.Text = "Impossible d’ouvrir la revue : " + error.Message; SetBusy(true); }
    }

    private async Task ShowCurrentAsync()
    {
        SetBusy(true); CancelDraw(); boxes.Clear(); DrawBoxes(); image.Source = null;
        UpdateSummary();
        if (position >= queue.Length)
        {
            detail.Text = "Fin de cette liste. Les images passées restent à examiner lors de la prochaine ouverture.";
            feedback.Text = queue.Length == 0 ? "Aucune image en attente pour le modèle actif. Enregistrez une session, puis arrêtez-la pour importer ses images." : $"{savedHere} image(s) vérifiée(s) et conservée(s) sur ce PC.";
            return;
        }
        var capture = queue[position];
        try
        {
            string path = coordinator.GetCaptureImagePath(capture.Id);
            var bitmap = await Task.Run(() => {
                if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Image trop volumineuse.");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var loaded = new BitmapImage(); loaded.BeginInit(); loaded.CacheOption = BitmapCacheOption.OnLoad;
                loaded.StreamSource = stream; loaded.EndInit();
                if ((long)loaded.PixelWidth * loaded.PixelHeight > 16_000_000) throw new InvalidDataException("Dimensions d’image excessives.");
                loaded.Freeze(); return loaded;
            });
            if (closed) return;
            canvas.Width = image.Width = bitmap.PixelWidth; canvas.Height = image.Height = bitmap.PixelHeight;
            image.Source = bitmap;
            boxes.AddRange(capture.Proposals.Where(b => b.IsValid));
            detail.Text = $"Image {position + 1}/{queue.Length} · session {capture.SessionId} · {capture.CapturedUtc.ToLocalTime():g}\n" +
                $"Motif : {Reason(capture.Reason)}. Boîtes proposées à vérifier : {boxes.Count}.";
            feedback.Text = "Ajoutez les cibles oubliées, retirez les erreurs, puis confirmez l’image entière.";
            DrawBoxes(); SetBusy(false);
        }
        catch (Exception error)
        {
            detail.Text = "Image illisible : " + error.Message;
            skip.IsEnabled = true;
        }
    }

    private static string Reason(string reason) => reason switch {
        "StableDetection" => "prédictions confiantes", "UncertainDetection" => "prédictions incertaines",
        "NoDetection" => "aucune prédiction", _ => "échantillon périodique" };

    private void UpdateSummary()
    {
        string sessions = string.Join(" · ", reviewedBySession.OrderBy(p => p.Key).Take(6).Select(p => $"{p.Key}: {p.Value}"));
        summary.Text = $"{reviewedBefore + savedHere} images vérifiées pour ce modèle · {Math.Max(0, queue.Length - position)} images dans la liste\n" +
            (sessions.Length > 0 ? "Vérifiées par session : " + sessions : "Aucune session vérifiée pour l’instant.") +
            (excludedModels > 0 ? $"\n{excludedModels} image(s) d’un autre modèle ou de provenance inconnue exclue(s)." : "");
    }

    private void SetBusy(bool value)
    {
        busy = value;
        bool available = !busy && position < queue.Length;
        approve.IsEnabled = available && boxes.Count > 0 && boxes.All(b => b.ClassId < classNames.Length);
        negative.IsEnabled = skip.IsEnabled = clear.IsEnabled = classPicker.IsEnabled = available;
    }

    private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, Math.Max(1, canvas.Width)), Math.Clamp(point.Y, 0, Math.Max(1, canvas.Height)));
    private void BeginDraw(object sender, MouseButtonEventArgs e)
    {
        if (busy || image.Source == null || boxes.Count >= 1000) return;
        dragStart = Clamp(e.GetPosition(canvas)); canvas.CaptureMouse();
        draft = new ShapeRectangle { Stroke = Brushes.White, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), IsHitTestVisible = false };
        canvas.Children.Add(draft); e.Handled = true;
    }
    private void MoveDraw(object sender, MouseEventArgs e)
    {
        if (dragStart is not Point start || draft == null) return;
        var point = Clamp(e.GetPosition(canvas));
        Canvas.SetLeft(draft, Math.Min(start.X, point.X)); Canvas.SetTop(draft, Math.Min(start.Y, point.Y));
        draft.Width = Math.Abs(point.X - start.X); draft.Height = Math.Abs(point.Y - start.Y);
    }
    private void EndDraw(object sender, MouseButtonEventArgs e)
    {
        if (dragStart is not Point start) return;
        var point = Clamp(e.GetPosition(canvas));
        if (Math.Abs(point.X - start.X) >= 3 && Math.Abs(point.Y - start.Y) >= 3)
            boxes.Add(new(classPicker.SelectedIndex, Math.Min(start.X, point.X) / canvas.Width,
                Math.Min(start.Y, point.Y) / canvas.Height, Math.Abs(point.X - start.X) / canvas.Width,
                Math.Abs(point.Y - start.Y) / canvas.Height));
        CancelDraw(); DrawBoxes(); e.Handled = true;
    }
    private void CancelDraw()
    {
        dragStart = null; canvas.ReleaseMouseCapture();
        if (draft != null) canvas.Children.Remove(draft); draft = null;
    }
    private void RemoveBox(object sender, MouseButtonEventArgs e)
    {
        if (busy || image.Source == null) return;
        CancelDraw(); var point = Clamp(e.GetPosition(canvas));
        int index = boxes.FindLastIndex(b => point.X >= b.X * canvas.Width && point.X <= (b.X + b.Width) * canvas.Width &&
            point.Y >= b.Y * canvas.Height && point.Y <= (b.Y + b.Height) * canvas.Height);
        if (index >= 0) { boxes.RemoveAt(index); DrawBoxes(); }
        e.Handled = true;
    }

    private void DrawBoxes()
    {
        foreach (var element in drawn) canvas.Children.Remove(element); drawn.Clear();
        foreach (var box in boxes)
        {
            var shape = new ShapeRectangle { Width = box.Width * canvas.Width, Height = box.Height * canvas.Height,
                Stroke = Brush("#62EDBC"), StrokeThickness = 2, IsHitTestVisible = false };
            Canvas.SetLeft(shape, box.X * canvas.Width); Canvas.SetTop(shape, box.Y * canvas.Height);
            canvas.Children.Add(shape); drawn.Add(shape);
            var label = Text(box.ClassId < classNames.Length ? classNames[box.ClassId] : $"Classe inconnue {box.ClassId} — retirer/corriger", 13);
            label.Background = Brush("#D0101426"); label.Foreground = Brush("#83FFD1"); label.IsHitTestVisible = false;
            Canvas.SetLeft(label, box.X * canvas.Width); Canvas.SetTop(label, Math.Max(0, box.Y * canvas.Height - 22));
            canvas.Children.Add(label); drawn.Add(label);
        }
        if (!busy) SetBusy(false);
    }

    private async Task SaveAsync(bool noTargets)
    {
        if (busy || position >= queue.Length) return;
        CancelDraw();
        if (!noTargets && (boxes.Count == 0 || boxes.Any(b => !b.IsValid || b.ClassId >= classNames.Length)))
        { feedback.Text = "Corrigez les classes et les boîtes, ou utilisez la confirmation explicite d’image sans cible."; return; }
        var capture = queue[position];
        var reviewed = noTargets ? Array.Empty<LearningBox>() : boxes.ToArray();
        SetBusy(true);
        try
        {
            await Task.Run(() => coordinator.RecordHumanReview(capture.Id, reviewed));
            savedHere++; reviewedBySession[capture.SessionId] = reviewedBySession.GetValueOrDefault(capture.SessionId) + 1;
            position++; if (!closed) await ShowCurrentAsync();
        }
        catch (Exception error) { feedback.Text = "Image non enregistrée : " + error.Message; if (!closed) SetBusy(false); }
    }
}
