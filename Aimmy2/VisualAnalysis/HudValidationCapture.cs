using Aimmy2.Adaptive;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Aimmy2.VisualAnalysis;

internal sealed record ValidationCaptureState(bool Active, int Frames, string? Report, string Message);
internal sealed class HudValidationCapture
{
    public static HudValidationCapture Instance { get; } = new();
    private readonly object gate = new();
    private string? directory;
    private DateTime deadline;
    private ValidationCaptureState state = new(false,0,null,"Un échantillon local permettra de valider les icônes et la visée.");
    public ValidationCaptureState State { get { lock(gate) return state; } }
    public void Start()
    {
        lock(gate)
        {
            if(state.Active) return;
            directory=Path.Combine(ObservationMode.DataDirectory,"validation",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,"context.json"),JsonSerializer.Serialize(PlayerSessionContext.Load(ObservationMode.DataDirectory)));
            deadline=DateTime.UtcNow.AddMinutes(2);
            state=new(true,0,null,"Remettez R6 au premier plan : collecte automatique de 20 images du centre et du HUD.");
        }
    }
    public void Tick()
    {
        lock(gate) if(state.Active && DateTime.UtcNow > deadline) Finish("Collecte arrêtée après 2 minutes d’attente.");
    }
    public void Stop() { lock(gate) if(state.Active) Finish("Échantillon partiel conservé après arrêt."); }
    public void Add(Bitmap center, Bitmap hud, DateTime capturedAt)
    {
        lock(gate)
        {
            if(!state.Active || directory==null) return;
            int index=state.Frames+1;
            center.Save(Path.Combine(directory,$"{index:00}-center.jpg"),ImageFormat.Jpeg);
            hud.Save(Path.Combine(directory,$"{index:00}-hud.jpg"),ImageFormat.Jpeg);
            File.AppendAllText(Path.Combine(directory,"frames.jsonl"),JsonSerializer.Serialize(new {Index=index,CapturedUtc=capturedAt,Labels="Unannotated",AdsState="Unknown"})+"\n");
            state=state with {Frames=index,Message=$"Échantillon : {index}/20. Alternez avec et sans visée."};
            if(index>=20) Finish("Échantillon prêt : envoyez ce ZIP pour valider la reconnaissance visuelle.");
        }
    }
    private void Finish(string message)
    {
        if(directory==null || state.Frames==0) { state=state with {Active=false,Message=message+" Aucune image collectée."}; return; }
        try
        {
            Directory.CreateDirectory(ObservationMode.ExportDirectory);
            string destination=Path.Combine(ObservationMode.ExportDirectory,"AutoAimmy-validation-"+Path.GetFileName(directory)+".zip");
            string temporary=destination+".tmp";
            ZipFile.CreateFromDirectory(directory,temporary);
            File.Move(temporary,destination);
            state=state with {Active=false,Report=destination,Message=message};
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException)
        { state=state with {Active=false,Message="Création du ZIP échouée ; les images restent dans data/validation. "+error.Message}; }
    }
}
