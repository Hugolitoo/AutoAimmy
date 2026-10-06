using Aimmy2.LocalAutomation;
using Aimmy2.LocalCapture;
using System.Globalization;
using System.Windows.Media;

namespace Aimmy2.Controls;

public partial class AutoAimmyMenuControl
{
    // Presentation reads existing state; it never starts capture, training or mouse output.
    private void RefreshDashboardSummary()
    {
        var session = LocalAutomationSession.Instance.State;
        var capture = LocalCaptureService.Instance.State;
        bool modelLoaded = global::Other.FileManager.AIManager?.IsLoaded == true;
        string tone = session.Calibrating || session.Active && !capture.Active ? "AutoAmber" :
            session.AssistanceEnabled || session.Active && capture.Status == "Recording" ? "AutoMint" : "AutoViolet";
        var accent = (Brush)FindResource(tone);
        StatusDot.Fill = accent;
        StatusBadgeText.Foreground = accent;
        StatusBadgeText.Text = session.Calibrating ? "Calibration" : session.AssistanceEnabled ? "Assistance active" :
            session.Active ? capture.Status == "Recording" ? "Observation" : "En attente de R6" : modelLoaded ? "Prêt" : "Modèle requis";
        CaptureSummaryText.Text = capture.Active ? capture.Status == "Recording" ? "En cours" : "En pause" : "En attente";
        CaptureCountText.Text = capture.FullFrames.ToString("N0", CultureInfo.CurrentCulture) + (capture.FullFrames == 1 ? " image du jeu" : " images du jeu");
        CalibrationSummaryText.Text = session.Calibrating ? "En cours" : session.Calibrated ? session.NeedsMeasurement ? "À mesurer" : "Prête" : "À faire";
        CalibrationSummaryText.Foreground = (Brush)FindResource(session.Calibrated && !session.NeedsMeasurement ? "AutoMint" : "AutoText");
        ProfilesSummaryText.Text = session.ProfileCount.ToString(CultureInfo.CurrentCulture);
        if (!session.Active && global::Other.FileManager.AIManager?.Observation == null)
            ObservationTitle.Text = modelLoaded ? "Prêt pour votre prochaine session" : "Commencez avec votre modèle";
        int reviewed = learningState?.ReviewedImages ?? 0;
        ReviewCountText.Text = reviewed.ToString("N0", CultureInfo.CurrentCulture) + " / 40";
        ReviewProgress.Value = System.Math.Min(40, reviewed);
    }
}
