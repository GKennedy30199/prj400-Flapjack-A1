using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.IO;




public class FlapjackA_NetworkManager : MonoBehaviour
{
    private readonly Queue<Action> _mainThread = new Queue<Action>();


    private void RunOnMainThread(Action a)
    {
        lock (_mainThread) _mainThread.Enqueue(a);
    }
    public enum AppState
    {
        Login,        // not connected
        Connecting,   // trying to connect / waiting
        Connected,    // TCP connected but not authed yet
        Ready         // authenticated
    }

    

    [Header("Auth UI")]
    public TMP_InputField passwordInput;
   

    private TcpClient tcp;
    private NetworkStream stream;
    private Thread tcpReadThread;
    

    private volatile bool connected;       // TCP socket is connected
    private volatile bool authenticated;   // server accepted auth
    private volatile bool shouldRun;
    private volatile bool hasPendingState;
    private AppState pendingState;
    private string pendingStatus;
 

    private readonly object sendLock = new object();

    public bool IsConnected => connected;
    public bool IsAuthenticated => authenticated;

    
    public bool IsReady => connected && authenticated;

    [SerializeField] private bool editorForceLocalhost = false;

    [SerializeField] private GameObject loginPanel;
    [SerializeField] private GameObject menuPanel;
    [SerializeField] private GameObject dicepanel;
    [SerializeField] private TMPro.TMP_Text statusText;

    // track state
    private volatile AppState state = AppState.Login;
    public AppState State => state;

    private int counterIndex = 0;

    public Texture2D testPlaymat;

    //public InputField passwordInput;
   // private volatile bool authenticated;

    [Header("Ports")]
    public int udpPort = 47777;
    public int tcpPortFallback = 48888;

    [Header("Discovery")]
    public float discoverySeconds = 4f;

    [Header("UI")]
    //public Text statusText;
    public Button retryButton;
    public Button connectButton;

    private volatile bool discovering;
    //private volatile bool connected;

    private string connectedIp;
   // private TcpClient tcp;
   // private NetworkStream stream;

    private Thread discoverThread;
    // private Thread tcpReadThread;


    //Counters data
    [Header("Counter Data")]
    public List<CounterData> counters = new List<CounterData>();
    public CounterData selectedCounter;

    [Header("Counter UI")]
    public GameObject counterListPanel;
    public GameObject counterActionPanel;
    public GameObject counterMovePanel;
    public GameObject counterNamePanel;
    public Transform counterListContainer;
    public GameObject counterListButtonPrefab;
    public TMP_InputField counterNameInput;

    private byte[] pendingCounterImageBytes;

    // Dice data

    [Header("Dice Counts")]
    private int pendingCoin = 0;
    private int pendingD4 = 0;
    private int pendingD6 = 0;
    private int pendingD8 = 0;
    private int pendingD10 = 0;
    private int pendingD12 = 0;
    private int pendingD20 = 0;
    [Header("Dice UI")]
    public TMPro.TMP_Text diceSelectionText;
    public GameObject rollDiceButton;
    public GameObject clearDiceButton;
    private int ReadDieCount(TMP_InputField input)
    {
        if (input == null) return 0;
        if (int.TryParse(input.text.Trim(), out int value))
            return Mathf.Max(0, value);
        return 0;
    }
    void Start()
    {
        SetState(AppState.Login, "Enter pairing code and connect.");

        retryButton.onClick.AddListener(RetryDiscovery);

        RefreshDiceUI();
        RetryDiscovery();
    }

    void OnDestroy()
    {
        ShutdownNetworking();
    }

    private void Update()
    {
        int ran = 0;
        while (true)
        {
            Action a = null;
            lock (_mainThread)
            {
                if (_mainThread.Count == 0) break;
                a = _mainThread.Dequeue();
            }
            a?.Invoke();
            ran++;
        }
        if (ran > 0) Debug.Log("UI actions ran: " + ran);
    }


    public void RetryDiscovery()
    {
        SetState(AppState.Connecting, "Searching for Flapjack B...");

        if (connected) ShutdownNetworking();

        if (discoverThread != null && discoverThread.IsAlive)
            return;

        discovering = true;
        discoverThread = new Thread(DiscoveryWorker) { IsBackground = true };
        discoverThread.Start();
    }

    private void DiscoveryWorker()
    {
        UdpClient udp = null;
        try
        {
            udp = new UdpClient();
            udp.EnableBroadcast = true;
            udp.Client.ReceiveTimeout = 500;

            var bcastIp = NetUtil.GetSubnetBroadcastAddress();
            var broadcast = new IPEndPoint(bcastIp, udpPort);

            byte[] discover = Encoding.UTF8.GetBytes("FLAPJACK_DISCOVER");

            DateTime end = DateTime.UtcNow.AddSeconds(discoverySeconds);

            while (!connected && DateTime.UtcNow < end)
            {
                udp.Send(discover, discover.Length, broadcast);

                var remote = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    byte[] resp = udp.Receive(ref remote);
                    string msg = Encoding.UTF8.GetString(resp).Trim();

                    if (msg.StartsWith("FLAPJACK_HERE:"))
                    {
                        // FLAPJACK_HERE:<name>:<port>
                        var parts = msg.Split(':');
                        int tcpPort = (parts.Length >= 3 && int.TryParse(parts[2], out var p))
                            ? p
                            : tcpPortFallback;

                        connectedIp = remote.Address.ToString();
                        discovering = false;

                        ConnectToResolvedTarget(connectedIp, tcpPort);

                        return;
                    }
                }
                catch (SocketException)
                {
                    // timeout, loop again
                }

                Thread.Sleep(250);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("Discovery error: " + e);
        }
        finally
        {
            try { udp?.Close(); } catch { }
            discovering = false;
        }
        //Debug.Log($"TRY TCP -> {connectedIp}:{tcpPort}");
    }

    private void ConnectTcp(string ip, int port)
    {

        // Clean reset before connecting
        DisconnectInternal();

        try
        {
            tcp = new TcpClient();
            tcp.NoDelay = true; // good for small messages
            tcp.Connect(ip, port);

            stream = tcp.GetStream();
            connected = true;
            SetState(AppState.Connected, "Connected. Authenticating...");

            authenticated = false;
            shouldRun = true;

            Debug.Log($"✅ TCP connected to {ip}:{port}");

            // Start the reader thread FIRST so we can receive auth_ok/auth_fail
            tcpReadThread = new Thread(TcpReadWorker) { IsBackground = true };
            tcpReadThread.Start();

            // Send auth right after connection
            string pw = SafePassword();
            Debug.Log($"AUTH sending password='{pw}' len={pw.Length}");
            SendLineSafe($"{{\"type\":\"auth\",\"password\":\"{EscapeJson(pw)}\"}}");

            Debug.Log("➡️ Sent auth message");
        }
        catch (Exception e)
        {
            Debug.LogError($"❌ TCP connect failed: {e}");
            DisconnectInternal();
        }
    }

    private void ConnectToResolvedTarget(string ip, int port)
    {
#if UNITY_EDITOR
   // Debug.Log("EDITOR: forcing localhost");
    //ip = "127.0.0.1";
#endif
        ConnectTcp(ip, port);
    }


    private void TcpReadWorker()
    {
        var buf = new byte[8192];
        var sb = new StringBuilder();

        try
        {
            while (shouldRun && tcp != null && tcp.Connected)
            {
                int read = stream.Read(buf, 0, buf.Length);
                if (read <= 0) break;

                sb.Append(Encoding.UTF8.GetString(buf, 0, read));

                while (true)
                {
                    int idx = sb.ToString().IndexOf('\n');
                    if (idx < 0) break;

                    string line = sb.ToString(0, idx).Trim('\r');
                    sb.Remove(0, idx + 1);

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    Debug.Log("⬅️ TCP line: " + line);
                    HandleServerLine(line);
                }
            }
        }
        catch (Exception e)
        {
            Debug.Log("TCP read ended: " + e.Message);
        }

        // If we exit loop, connection ended
        Debug.Log("🔌 TCP disconnected.");
        DisconnectInternal();
    }

    private void HandleServerLine(string line)
    {
        if (line.Contains("\"type\":\"auth_ok\""))
        {
            authenticated = true;
            SetState(AppState.Ready, "Connected ✅");
            RequestFullState();
            return;
        }

        if (line.Contains("\"type\":\"auth_fail\""))
        {
            authenticated = false;
            SetState(AppState.Login, "Wrong code. Try again.");
            DisconnectInternal();
            return;
        }
        if (line.Contains("\"type\":\"full_state\""))
        {
            string stateJsonEscaped = ExtractString(line, "state");
            string stateJson = UnescapeJsonString(stateJsonEscaped);

            HandleFullState(stateJson);
            return;
        }
        if (line.Contains("not_authenticated"))
        {
            Debug.LogWarning("Server says not_authenticated. Resending auth...");
            string pw = SafePassword();
            SendLineSafe($"{{\"type\":\"auth\",\"password\":\"{EscapeJson(pw)}\"}}");
            return;
        }
    }


    public void SendLineSafe(string line)
    {
        if (!connected || stream == null) return;

        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (sendLock)
        {
            try
            {
                stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception e)
            {
                Debug.LogError("Send failed: " + e.Message);
                DisconnectInternal();
            }
        }
    }


    private void ShutdownNetworking()
    {
        discovering = false;
        connected = false;

        try { stream?.Close(); } catch { }
        try { tcp?.Close(); } catch { }

        stream = null;
        tcp = null;
    }

    //Playmat code
    public void SendTestPlaymat()
    {
        if (!IsReady || testPlaymat == null) return;

        byte[] png = testPlaymat.EncodeToPNG();
        SendPlaymatBytes(png);
    }
    public void PickAndSendPlaymat()
    {
        if (!IsReady)
        {
            Debug.LogWarning("Not ready (connect + auth first).");
            return;
        }

        if (!NativeFilePicker.CheckPermission())
        {
            Debug.LogError("Storage permission not granted.");
            return;
        }

        NativeFilePicker.PickFile((path) =>
        {
            if (string.IsNullOrEmpty(path))
            {
                Debug.Log("User cancelled playmat pick.");
                return;
            }

            try
            {
                byte[] fileBytes = File.ReadAllBytes(path);

                // For now, assume image file
                SendPlaymatBytes(fileBytes);

                Debug.Log("Picked and sent playmat: " + path);
            }
            catch (System.Exception e)
            {
                Debug.LogError("Failed to read playmat file: " + e);
            }

        }, new string[] { "image/png", "image/jpeg" });
    }
    public void SendPlaymatBytes(byte[] imageBytes)
    {
        if (!IsReady || imageBytes == null || imageBytes.Length == 0) return;

        string header = $"{{\"type\":\"playmat_begin\",\"size\":{imageBytes.Length}}}";
        SendLineSafe(header);

        lock (sendLock)
        {
            stream.Write(imageBytes, 0, imageBytes.Length);
        }

        Debug.Log($"✅ Sent playmat bytes={imageBytes.Length}");
    }

    //dice code

    public void AddCoin() { pendingCoin++; RefreshDiceUI(); }
    public void AddD4() { pendingD4++; RefreshDiceUI(); }
    public void AddD6() { pendingD6++; RefreshDiceUI(); }
    public void AddD8() { pendingD8++; RefreshDiceUI(); }
    public void AddD10() { pendingD10++; RefreshDiceUI(); }
    public void AddD12() { pendingD12++; RefreshDiceUI(); }
    public void AddD20() { pendingD20++; RefreshDiceUI(); }

    public void RefreshDiceUI()
    {
        if (diceSelectionText != null)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            if (pendingCoin > 0) sb.AppendLine($"Coin x{pendingCoin}");
            if (pendingD4 > 0) sb.AppendLine($"d4 x{pendingD4}");
            if (pendingD6 > 0) sb.AppendLine($"d6 x{pendingD6}");
            if (pendingD8 > 0) sb.AppendLine($"d8 x{pendingD8}");
            if (pendingD10 > 0) sb.AppendLine($"d10 x{pendingD10}");
            if (pendingD12 > 0) sb.AppendLine($"d12 x{pendingD12}");
            if (pendingD20 > 0) sb.AppendLine($"d20 x{pendingD20}");

            if (sb.Length == 0)
                sb.Append("No dice selected");

            diceSelectionText.text = sb.ToString();
        }

        bool hasDice = (pendingCoin + pendingD4 + pendingD6 + pendingD8 + pendingD10 + pendingD12 + pendingD20) > 0;

        if (rollDiceButton != null) rollDiceButton.SetActive(hasDice);
        if (clearDiceButton != null) clearDiceButton.SetActive(hasDice);
    }
    public void RollDice()
    {
        if (!IsReady) return;

        int total = pendingCoin + pendingD4 + pendingD6 + pendingD8 + pendingD10 + pendingD12 + pendingD20;
        if (total <= 0) return;

        SendLineSafe(
            $"{{\"type\":\"roll_dice\",\"coin\":{pendingCoin},\"d4\":{pendingD4},\"d6\":{pendingD6},\"d8\":{pendingD8},\"d10\":{pendingD10},\"d12\":{pendingD12},\"d20\":{pendingD20}}}"
        );
    }
    public void ClearDice()
    {
        pendingCoin = 0;
        pendingD4 = 0;
        pendingD6 = 0;
        pendingD8 = 0;
        pendingD10 = 0;
        pendingD12 = 0;
        pendingD20 = 0;

        RefreshDiceUI();

        if (IsReady)
            SendLineSafe("{\"type\":\"clear_dice\"}");
    }
    //disconnect
    public void Disconnect()
    {
        DisconnectInternal();
    }

    private void DisconnectInternal()
    {
        shouldRun = false;
        authenticated = false;
        connected = false;

        try { stream?.Close(); } catch { }
        try { tcp?.Close(); } catch { }

        stream = null;
        tcp = null;
        SetState(AppState.Login, "Disconnected. Enter code and retry.");

    }

    private void SetState(AppState newState, string status = null)
    {
        // Safe to call from ANY thread
        RunOnMainThread(() => SetState_Internal(newState, status));
    }

    private void SetState_Internal(AppState newState, string status = null)
    {
        state = newState;
        bool ready = (newState == AppState.Ready);

        if (loginPanel != null) loginPanel.SetActive(!ready);
        if (menuPanel != null) menuPanel.SetActive(ready);

        if (statusText != null && status != null)
            statusText.text = status;
    }

    //Menu Methods
    public void OpenCounterListMenu()
    {
        if (counterListPanel != null) counterListPanel.SetActive(true);
        if (counterActionPanel != null) counterActionPanel.SetActive(false);
        if (counterMovePanel != null) counterMovePanel.SetActive(false);
        if (counterNamePanel != null) counterNamePanel.SetActive(false);

        RefreshCounterListUI();
    }

    public void OpenCounterActionMenu(CounterData counter)
    {
        selectedCounter = counter;

        if (counterListPanel != null) counterListPanel.SetActive(false);
        if (counterActionPanel != null) counterActionPanel.SetActive(true);
        if (counterMovePanel != null) counterMovePanel.SetActive(false);
        if (counterNamePanel != null) counterNamePanel.SetActive(false);
    }

    public void OpenCounterMoveMenu()
    {
        if (selectedCounter == null) return;

        if (counterListPanel != null) counterListPanel.SetActive(false);
        if (counterActionPanel != null) counterActionPanel.SetActive(false);
        if (counterMovePanel != null) counterMovePanel.SetActive(true);
        if (counterNamePanel != null) counterNamePanel.SetActive(false);
    }

    public void BackToCounterList()
    {
        if (counterListPanel != null) counterListPanel.SetActive(true);
        if (counterActionPanel != null) counterActionPanel.SetActive(false);
        if (counterMovePanel != null) counterMovePanel.SetActive(false);
        if (counterNamePanel != null) counterNamePanel.SetActive(false);
        if (menuPanel != null) menuPanel.SetActive(false);
        
    }
     
   public void BackToCounterAction()
    {
        if (counterListPanel != null) counterListPanel.SetActive(false);
        if (counterActionPanel != null) counterActionPanel.SetActive(true);
        if (counterMovePanel != null) counterMovePanel.SetActive(false);
        if (counterNamePanel != null) counterNamePanel.SetActive(false);
        if (menuPanel != null) menuPanel.SetActive(false);
    }
    public void BackToMainMenu()
    {
       
        if (counterListPanel != null) counterListPanel.SetActive(false);
        if (counterActionPanel != null) counterActionPanel.SetActive(false);
        if (counterMovePanel != null) counterMovePanel.SetActive(false);
        if (counterNamePanel != null) counterNamePanel.SetActive(false);
        if (dicepanel != null) dicepanel.SetActive(false);
        if (menuPanel != null) menuPanel.SetActive(true);
    }
    //Counter Methods
    public void BeginAddCounter()
    {
        if (!IsReady)
        {
            Debug.LogWarning("Not ready (connect + auth first).");
            return;
        }

        if (!NativeFilePicker.CheckPermission())
        {
            Debug.LogError("Storage permission not granted.");
            return;
        }

        NativeFilePicker.PickFile((path) =>
        {
            if (string.IsNullOrEmpty(path))
            {
                Debug.Log("User cancelled.");
                return;
            }

            try
            {
                pendingCounterImageBytes = File.ReadAllBytes(path);

                RunOnMainThread(() =>
                {
                    if (counterNameInput != null)
                        counterNameInput.text = "";

                    if (counterListPanel != null) counterListPanel.SetActive(false);
                    if (counterActionPanel != null) counterActionPanel.SetActive(false);
                    if (counterMovePanel != null) counterMovePanel.SetActive(false);
                    if (counterNamePanel != null) counterNamePanel.SetActive(true);
                });

                Debug.Log("Counter image selected. Waiting for name.");
            }
            catch (System.Exception e)
            {
                Debug.LogError("Failed to read file: " + e);
            }
        }, new string[] { "image/png" });
    }

    public void SelectCounter(CounterData counter)
    {
        if (counter == null) return;

        selectedCounter = counter;

        SendLineSafe(
            $"{{\"type\":\"counter_select\",\"id\":\"{counter.id}\"}}"
        );

        OpenCounterActionMenu(counter);
    }
    public void ConfirmCreateCounter()
    {
        if (!IsReady) return;

        if (pendingCounterImageBytes == null || pendingCounterImageBytes.Length == 0)
        {
            Debug.LogWarning("No counter image selected.");
            return;
        }

        string counterName = (counterNameInput != null) ? counterNameInput.text.Trim() : "";

        if (string.IsNullOrEmpty(counterName))
        {
            Debug.LogWarning("Counter name is empty.");
            return;
        }

        counterIndex++;
        string id = $"c{counterIndex}";

        CounterData newCounter = new CounterData
        {
            id = id,
            displayName = counterName,
            x = 0.5f,
            y = 0.5f
        };

        counters.Add(newCounter);
        selectedCounter = newCounter;

        SendCounterPngBytesWithId(id, newCounter.displayName, pendingCounterImageBytes, newCounter.x, newCounter.y, 0.10f);

        pendingCounterImageBytes = null;

        if (counterNameInput != null)
            counterNameInput.text = "";

        RefreshCounterListUI();
        OpenCounterListMenu();
    }
    public void CancelCreateCounter()
    {
        pendingCounterImageBytes = null;

        if (counterNameInput != null)
            counterNameInput.text = "";

        OpenCounterListMenu();
    }
    public void SendCounterPngBytesWithId(string id, string displayName, byte[] pngBytes, float x = 0.5f, float y = 0.5f, float size = 0.10f)
    {
        if (!IsReady || pngBytes == null || pngBytes.Length == 0) return;

        string safeName = EscapeJson(displayName ?? "");

        string header =
            $"{{\"type\":\"counter_begin\",\"id\":\"{id}\",\"name\":\"{safeName}\",\"x\":{x},\"y\":{y},\"size\":{size},\"bytes\":{pngBytes.Length}}}";

        SendLineSafe(header);

        lock (sendLock)
        {
            stream.Write(pngBytes, 0, pngBytes.Length);
        }

        Debug.Log($"✅ Sent counter {id} ({displayName}) bytes={pngBytes.Length}");
    }
    public void SendCounterPngBytes(byte[] pngBytes, float x = 0.5f, float y = 0.5f, float size = 0.10f)
    {
        if (!IsReady || pngBytes == null || pngBytes.Length == 0) return;

        counterIndex++;
        string id = $"c{counterIndex}";

        SendCounterPngBytesWithId(id, id, pngBytes, x, y, size);
    }

    public void RefreshCounterListUI()

    {
        if (counterListContainer == null || counterListButtonPrefab == null) return;

        // Remove old buttons
        for (int i = counterListContainer.childCount - 1; i >= 0; i--)
        {
            Destroy(counterListContainer.GetChild(i).gameObject);
        }

        // Rebuild buttons for each counter
        foreach (var counter in counters)
        {
            GameObject btnObj = Instantiate(counterListButtonPrefab, counterListContainer);

            TMP_Text label = btnObj.GetComponentInChildren<TMP_Text>();
            if (label != null)
                label.text = counter.displayName;

            Button btn = btnObj.GetComponent<Button>();
            if (btn != null)
            {
                CounterData captured = counter;
                btn.onClick.AddListener(() => SelectCounter(captured));
            }
        }
    }

    public void ClearSelectedCounter()
    {
        selectedCounter = null;
        SendLineSafe("{\"type\":\"counter_select\",\"id\":\"\"}");
    }

    [SerializeField] private float moveStep = 0.10f;

    //Move Counter Methods
    public void MoveUp()
    {
        MoveSelectedBy(0f, moveStep);
    }

    public void MoveDown()
    {
        MoveSelectedBy(0f, -moveStep);
    }

    public void MoveLeft()
    {
        MoveSelectedBy(-moveStep, 0f);
    }

    public void MoveRight()
    {
        MoveSelectedBy(moveStep, 0f);
    }

    public void MoveSelectedBy(float dx, float dy)
    {
        if (selectedCounter == null) return;

        selectedCounter.x = Mathf.Clamp01(selectedCounter.x + dx);
        selectedCounter.y = Mathf.Clamp01(selectedCounter.y + dy);

        SendLineSafe(
            $"{{\"type\":\"counter_move\",\"id\":\"{selectedCounter.id}\",\"x\":{selectedCounter.x},\"y\":{selectedCounter.y}}}"
        );
    }
    //Delete Counter Method
    public void DeleteSelectedCounter()
    {
        if (selectedCounter == null) return;

        SendLineSafe(
            $"{{\"type\":\"counter_delete\",\"id\":\"{selectedCounter.id}\"}}"
        );

        counters.Remove(selectedCounter);
        selectedCounter = null;

        RefreshCounterListUI();
        OpenCounterListMenu();
    }

    //Edit Counter Method
    public void EditSelectedCounter()
    {
        if (selectedCounter == null) return;

        NativeFilePicker.PickFile((path) =>
        {
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                byte[] pngBytes = System.IO.File.ReadAllBytes(path);

                SendCounterPngBytesWithId(
                 selectedCounter.id,
                 selectedCounter.displayName,
                 pngBytes,
                 selectedCounter.x,
                 selectedCounter.y,
                  0.10f
                );
            }
            catch (System.Exception e)
            {
                Debug.LogError(e);
            }

        }, new string[] { "image/png" });

    }

    //Resize Counter Method
    [SerializeField] private float resizeStep = 0.02f;

    public void IncreaseSelectedCounterSize()
    {
        ResizeSelectedCounterBy(resizeStep);
    }

    public void DecreaseSelectedCounterSize()
    {
        ResizeSelectedCounterBy(-resizeStep);
    }

    public void ResizeSelectedCounterBy(float delta)
    {
        if (selectedCounter == null) return;

        selectedCounter.size = Mathf.Clamp(selectedCounter.size + delta, 0.02f, 0.5f);

        SendLineSafe(
            $"{{\"type\":\"counter_resize\",\"id\":\"{selectedCounter.id}\",\"size\":{selectedCounter.size.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"
        );
    }
    //misc
    public string SafePassword()
    {
        // Never crash if passwordInput isn't wired
        if (passwordInput == null) return "";
        return passwordInput ? (passwordInput.text ?? "").Trim() : "";
    }

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
    public void OnDisconnectClicked()
    {
        DisconnectInternal();
        // DisconnectInternal will push you back to Login state
    }

    public void DuplicateSelectedCounter()
    {
        if (selectedCounter == null) return;

        SendLineSafe(
            $"{{\"type\":\"counter_duplicate\",\"id\":\"{selectedCounter.id}\"}}"
        );

        // Best to let sync repopulate A correctly afterward
        RequestFullState();
    }
    public void ToggleSelectedCounterLock()
    {
        if (selectedCounter == null) return;

        selectedCounter.isLocked = !selectedCounter.isLocked;
        int lockedValue = selectedCounter.isLocked ? 1 : 0;

        SendLineSafe(
            $"{{\"type\":\"counter_lock\",\"id\":\"{selectedCounter.id}\",\"locked\":{lockedValue}}}"
        );
    }

    private void HandleFullState(string stateJson)
    {
        try
        {
            BoardState boardState = JsonUtility.FromJson<BoardState>(stateJson);

            if (boardState == null)
            {
                Debug.LogError("HandleFullState: boardState is NULL");
                return;
            }

            RunOnMainThread(() =>
            {
                counters.Clear();
                selectedCounter = null;

                if (boardState.counters != null)
                {
                    foreach (var remoteCounter in boardState.counters)
                    {
                        if (remoteCounter == null) continue;

                        CounterData localCounter = new CounterData
                        {
                            id = remoteCounter.id,
                            displayName = string.IsNullOrWhiteSpace(remoteCounter.displayName) ? remoteCounter.id : remoteCounter.displayName,
                            x = remoteCounter.x,
                            y = remoteCounter.y,
                            size = remoteCounter.size,
                            isLocked = remoteCounter.isLocked
                        };

                        counters.Add(localCounter);

                        if (!string.IsNullOrEmpty(boardState.selectedCounterId) &&
                            remoteCounter.id == boardState.selectedCounterId)
                        {
                            selectedCounter = localCounter;
                        }
                    }
                }

                RefreshCounterListUI();

                Debug.Log($"✅ Full state applied on A. Counters={counters.Count}, selected={boardState.selectedCounterId}");
            });
        }
        catch (Exception e)
        {
            Debug.LogError("HandleFullState exception: " + e);
        }
    }
    private string ExtractString(string json, string key)
    {
        string pattern = $"\"{key}\":\"";
        int start = json.IndexOf(pattern, StringComparison.Ordinal);
        if (start < 0) return "";

        start += pattern.Length;
        int end = json.IndexOf("\"", start);
        if (end < 0) return "";

        return json.Substring(start, end - start);
    }
    private string UnescapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }
    public void RequestFullState()
    {
        if (!IsReady) return;

        SendLineSafe("{\"type\":\"request_full_state\"}");
        Debug.Log("➡️ Requested full state from B");
    }

}
public static class NetUtil
{
    public static IPAddress GetSubnetBroadcastAddress()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var ipProps = ni.GetIPProperties();
            foreach (var ua in ipProps.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                if (mask == null) continue;

                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcast[i] = (byte)(ip[i] | (mask[i] ^ 255));

                return new IPAddress(broadcast);
            }
        }

        return IPAddress.Broadcast; // fallback
    }
}

public class AndroidMulticastLock : MonoBehaviour
{
#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject multicastLock;
#endif

    void Awake()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
            using (var wifiManager = context.Call<AndroidJavaObject>("getSystemService", "wifi"))
            {
                multicastLock = wifiManager.Call<AndroidJavaObject>("createMulticastLock", "flapjack_lock");
                multicastLock.Call("setReferenceCounted", true);
                multicastLock.Call("acquire");
                Debug.Log("MulticastLock acquired");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Failed to acquire MulticastLock: " + e.Message);
        }
#endif
    }

    void OnDestroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            if (multicastLock != null)
                multicastLock.Call("release");
        }
        catch { }
#endif
    }
}



