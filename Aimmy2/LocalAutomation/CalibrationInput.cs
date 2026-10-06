namespace Aimmy2.LocalAutomation;

// Button/character state sampled with the image, before inference work.
internal readonly record struct CalibrationInput(bool ActivationHeld, bool Firing, bool Walking, bool RightHeld);
