using UnityEngine;
using Unity.Services.Core;
using Unity.Services.Authentication;
using System.Threading.Tasks;

public class SessionInit : MonoBehaviour
{
    // Prevent auto-init for pure offline; only init when Host/Join calls EnsureReady.
    [SerializeField] bool autoInitializeAtStartup = false;

    public static bool Ready { get; private set; }
    public static bool OfflineMode { get; private set; }

    async void Awake()
    {
        if (autoInitializeAtStartup && !OfflineMode)
        {
            await EnsureReady();
        }
    }

    public static void RequestOffline()
    {
        OfflineMode = true;
    }

    public static void RequestOnline()
    {
        OfflineMode = false;
    }

    public static async Task EnsureReady()
    {
        if (OfflineMode) return; // do not initialize in offline mode
        if (Ready) return;
        await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
        Ready = true;
    }
}
