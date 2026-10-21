namespace RemoteMouse.Core.Pairing;

public static class VerificationCode
{
    public static string Create() => Random.Shared.Next(0, 1_000_000).ToString("D6");

    public static string Format(string code)
        => code.Length == 6 ? $"{code[..3]} - {code[3..]}" : code;
}
