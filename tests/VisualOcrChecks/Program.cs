using System.Drawing;
using System.Reflection;
using Aimmy2.Adaptive;

if (args.Contains("--resources"))
{
    Console.WriteLine(string.Join("\n", typeof(GameplayEvent).Assembly.GetManifestResourceNames().Where(n=>n.Contains("winrt",StringComparison.OrdinalIgnoreCase) || n.Contains("windows.sdk",StringComparison.OrdinalIgnoreCase))));
    return;
}
using var bitmap = new Bitmap(850, 400);
using (var graphics = Graphics.FromImage(bitmap))
{
    graphics.Clear(Color.Black);
    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    using var font = new Font("Arial", 36, FontStyle.Bold);
    graphics.DrawString("WEAPON M4\nSCOPE 2.5 X ZOOM", font, Brushes.White, 20, 30);
    // A separate ammo row with a larger magazine count exercises real OCR
    // word geometry; this synthetic HUD does not establish R6 accuracy.
    using var magazineFont = new Font("Arial", 36, FontStyle.Bold);
    using var reserveFont = new Font("Arial", 24, FontStyle.Bold);
    graphics.DrawString("30", magazineFont, Brushes.White, 20, 250);
    graphics.DrawString("120", reserveFont, Brushes.White, 150, 270);
}
var type = typeof(GameplayEvent).Assembly.GetType("Aimmy2.VisualAnalysis.LiveHudObserver")!;
var method = type.GetMethod("ReadTextAsync", BindingFlags.Public | BindingFlags.Static)!;
var text = await (Task<string>)method.Invoke(null, new object[] { bitmap })!;
var parsed = HudTextParser.Parse(text);
if (parsed.Weapon != "M4" || parsed.Scope != "2.5x") throw new Exception("Actual Windows OCR could not read the test HUD: " + text);
Console.WriteLine("PASS: actual local Windows OCR reads rendered weapon and scope text; no screenshots stored or sent.");
var layoutMethod = type.GetMethod("ReadLayoutAsync", BindingFlags.Public | BindingFlags.Static)!;
var layout = await (Task<HudReadout>)layoutMethod.Invoke(null, new object[] { bitmap })!;
if (AmmoHudParser.Parse(layout) != (30, 120)) throw new Exception("Actual OCR ammunition readout not parsed: " + layout.Text);
Console.WriteLine("PASS: actual Windows OCR ammunition layout parsed from a generated HUD.");
string sampleRoot=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"AutoAimmy-sample-checks-"+Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("AUTOAIMMY_DATA_DIR",System.IO.Path.Combine(sampleRoot,"data"));
var sampleType=typeof(GameplayEvent).Assembly.GetType("Aimmy2.VisualAnalysis.HudValidationCapture")!;
var sample=sampleType.GetProperty("Instance")!.GetValue(null)!;
sampleType.GetMethod("Start")!.Invoke(sample,null);
for(int i=0;i<20;i++) sampleType.GetMethod("Add")!.Invoke(sample,new object[]{bitmap,bitmap,DateTime.UtcNow});
var sampleState=sampleType.GetProperty("State")!.GetValue(sample)!;
string report=(string)sampleState.GetType().GetProperty("Report")!.GetValue(sampleState)!;
using(var zip=System.IO.Compression.ZipFile.OpenRead(report))
    if(zip.Entries.Count!=42 || zip.GetEntry("frames.jsonl")==null || zip.GetEntry("context.json")==null) throw new Exception("Bounded validation sample ZIP invalid");
Console.WriteLine("PASS: optional validation capture produces exactly 20 image pairs plus timestamps/context, using generated images only.");
if (args.Contains("--live-probe"))
{
    Aimmy2.Class.DisplayManager.Initialize();
    var instance = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    type.GetMethod("Start")!.Invoke(instance, null);
    await Task.Delay(3500);
    var observed = (VisualHudObservation)type.GetProperty("Latest")!.GetValue(instance)!;
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(observed));
    ((IDisposable)instance).Dispose();
    if (observed.Status == "Unavailable") throw new Exception("Actual HUD reader unavailable: " + observed.Detail);
    Console.WriteLine("PASS: live reader foreground gating/capture executes without storing images or transcripts.");
}
