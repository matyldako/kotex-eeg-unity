using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

[Serializable]
public class BciData
{
    public float focus;
    public float relax;
    public int command;
}

public class BciUdpReceiver : MonoBehaviour
{
    [SerializeField] private int port = 5005;

    // Aktualne dane gotowe do odczytu z innych skryptów
    public BciData LatestData { get; private set; } = new BciData();

    private UdpClient _udpClient;
    private Thread _receiveThread;
    private volatile bool _isRunning;

    private void Start()
    {
        _isRunning = true;
        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
        _receiveThread.Start();
    }

    private void ReceiveLoop()
    {
        try
        {
            _udpClient = new UdpClient(port);
            IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (_isRunning)
            {
                byte[] data = _udpClient.Receive(ref remoteEndPoint);
                string json = Encoding.UTF8.GetString(data);

                // JsonUtility jest thread-safe dla prostych klas [Serializable]
                LatestData = JsonUtility.FromJson<BciData>(json);
            }
        }
        catch (SocketException)
        {
            // Ignorujemy wyj¹tek rzucany przy zamykaniu socketu w OnDestroy
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BCI UDP] B³¹d odbioru: {e.Message}");
        }
    }

    private void Update()
    {
        // Przyk³ad u¿ycia w logice gry:
        if (LatestData.focus > 0.75f)
        {
            // np. przyspiesz obiekt lub zmieñ shader
        }
    }

    private void OnDestroy()
    {
        _isRunning = false;
        _udpClient?.Close();
        _receiveThread?.Abort();
    }
}