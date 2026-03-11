using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

public class PairingPassword : MonoBehaviour
{
    public Text passwordText; // or TMP_Text if you're using TMP
    public string Password { get; private set; }

    void Awake()
    {
        Password = GenerateNumericCode(6); // "482913"
        if (passwordText != null)
            passwordText.text = $"Pairing Code: {Password}";
    }

    private string GenerateNumericCode(int digits)
    {
        // cryptographically strong random digits
        var bytes = new byte[digits];
        RandomNumberGenerator.Fill(bytes);

        var sb = new StringBuilder(digits);
        for (int i = 0; i < digits; i++)
            sb.Append((bytes[i] % 10).ToString());

        return sb.ToString();
    }
}

