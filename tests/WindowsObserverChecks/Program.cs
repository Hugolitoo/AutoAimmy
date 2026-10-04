using Aimmy2.Adaptive;

for (int i = 0; i < 3; i++)
{
    using var observer = new RawMouseObserver();
    if (!observer.TryRead(out _, out _, out _)) throw new Exception(observer.Failure ?? "Raw mouse registration unavailable");
    await Task.Delay(40);
    if (!observer.TryRead(out _, out _, out _)) throw new Exception("Observer stopped unexpectedly");
}
Console.WriteLine("PASS: passive Windows mouse registration, sampling and repeated shutdown. No input generated.");
