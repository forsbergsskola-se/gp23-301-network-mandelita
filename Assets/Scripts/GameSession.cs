using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GameSession : MonoBehaviour
{
    private const int UDPPortNumber = 50000;
    private const int TcpPortNumber = 44446;
    private bool finishedLoading;
    private PlayerController playerController;
    public bool isServer;

    #region ------ Client -------
    private IPEndPoint serverEndpointUDP;
    private IPEndPoint serverEndpointTCP;
    private UdpClient udpClient;
    private TcpClient tcpClient;
    #endregion

    #region ------ Server -------
    private Dictionary<IPEndPoint, OpponentController> opponents = new();
    private List<IPEndPoint> clients = new();
    private TcpListener tcpListener;
    private bool udpReady;

    #endregion

 
    private void FixedUpdate()
    {
        if (!finishedLoading || !udpReady) return; 

        if (!isServer)
        {
            SendPositionToServer();
            ReceiveOpponentUpdates();
        }
        else
        {
            ReceivePositions();
        }
    }

    private IEnumerator ReceivePositions()
    {
        Debug.Log("Server listening for positions...");

        while (true)
        {
            if (udpClient.Available > 0) // Check if there are any packets available
            {
                try
                {
                    // Receive a UDP packet
                    var receiveResult = udpClient.ReceiveAsync().Result; // Blocking call here
                    var fromEndpoint = receiveResult.RemoteEndPoint;
                    var receivedBytes = receiveResult.Buffer;
                    var receivedJson = Encoding.UTF8.GetString(receivedBytes);

                    Debug.Log($"Received position from {fromEndpoint}");

                    var playerState = JsonUtility.FromJson<PlayerState>(receivedJson);
                    EnsureOpponentAndUpdatePosition(fromEndpoint, playerState.position, playerState.size);
                    BroadcastOpponentStates(); 
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error receiving UDP packets: {ex.Message}");
                }
            }
        
            yield return null; // Yield to allow other processes to run
        }
    }




    private void BroadcastOpponentStates()
    {
        foreach (var opponent in opponents)
        {
            var state = new PlayerState(opponent.Value.transform.position, opponent.Value.GetComponent<Blob>().Size);
            var stateJson = JsonUtility.ToJson(state);
            var bytes = Encoding.UTF8.GetBytes(stateJson);

            Debug.Log($"Broadcasting opponent state to all clients");

            foreach (var client in clients)
            {
                if (client != null)
                {
                    udpClient.SendAsync(bytes, bytes.Length, client);
                }
            }
        }
    }

    
    private async void SendPositionToServer()
    {
        if (!udpReady) return; 

        var position = playerController.transform.position;
        var size = playerController.GetComponent<Blob>().Size;

        var state = new PlayerState(position, size);
        var stateJson = JsonUtility.ToJson(state);
        var bytes = Encoding.UTF8.GetBytes(stateJson);

        Debug.Log("Client sending position update...");

        try
        {
            await udpClient.SendAsync(bytes, bytes.Length, serverEndpointUDP);
            Debug.Log("Client sent position!");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error sending UDP packet: {ex.Message}");
        }
    }

    
    private void ReceiveOpponentUpdates()
    {
        if (!udpReady) return;
        try
        {
            if (udpClient.Available > 0)
            {
                var receiveResult = udpClient.Receive(ref serverEndpointUDP);
                var receivedJson = Encoding.UTF8.GetString(receiveResult);
                var opponentState = JsonUtility.FromJson<PlayerState>(receivedJson);

                Debug.Log($"Client received opponent update");

                EnsureOpponentAndUpdatePosition(serverEndpointUDP, opponentState.position, opponentState.size);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error receiving UDP packets: {ex.Message}");
        }
    }
    
    private void EnsureOpponentAndUpdatePosition(IPEndPoint opponentEndpoint, Vector3 position, float size)
    {
        if (!opponents.TryGetValue(opponentEndpoint, out var opponentController))
        {
            Debug.Log($"Spawning new opponent for {opponentEndpoint}");
            opponentController = SpawnOpponent();  
            opponents[opponentEndpoint] = opponentController;
        }

        if (opponentController != null)
        {
            Debug.Log($"Updating opponent position for {opponentEndpoint}");
            opponentController.UpdatePosition(position, size); 
        }
    }

    
    public static void HostGame()
    {
        try
        {
            var session = CreateNew();
            session.isServer = true;

            session.udpClient = new UdpClient(UDPPortNumber); 
            Debug.Log("UDP Listener started on port 50000");

            session.tcpListener = new TcpListener(IPAddress.Any, TcpPortNumber); 
            session.tcpListener.Start();
            Debug.Log("TCP Listener started on port " + TcpPortNumber);

            session.StartCoroutine(session.Co_AcceptClients());
            session.StartCoroutine(session.Co_LaunchGame());

            Debug.Log("HostGame successfully started");
        }
        catch (Exception ex)
        {
            Debug.LogError("Error in HostGame: " + ex.Message);
        }
    }

    private IEnumerator Co_AcceptClients()
    {
        Debug.Log("Waiting for TCP clients to connect...");

        while (true)
        {
            var task = tcpListener.AcceptTcpClientAsync();
            while (!task.IsCompleted)
            {
                yield return null;
            }

            var client = task.Result;
            var clientEndpoint = (IPEndPoint)client.Client.RemoteEndPoint;
        
            Debug.Log($"Client connected via TCP from {clientEndpoint}"); 

            clients.Add(clientEndpoint);

            // Spawn opponent for this client (on server)
            var newOpponent = SpawnOpponent();
            opponents[clientEndpoint] = newOpponent;

            Debug.Log($"Opponent spawned for {clientEndpoint}"); 

            yield return null;
        }
    }
    
    private IEnumerator Co_LaunchGame()
    {
        yield return SceneManager.LoadSceneAsync("Game");
        playerController = SpawnPlayer();
        finishedLoading = true;
    }

    public static void JoinGame(string hostName)
    {
        var session = CreateNew();
        session.isServer = false;

        try
        {
            session.udpClient = new UdpClient();  
            session.serverEndpointUDP = GetIPEndPoint(hostName, UDPPortNumber);  
            Debug.Log("UDP client initialized");

            session.tcpClient = new TcpClient();
            session.serverEndpointTCP = GetIPEndPoint(hostName, TcpPortNumber);
            Debug.Log("TCP client initialized, server endpoint: " + session.serverEndpointTCP);
        }
        catch (Exception ex)
        {
            Debug.LogError("Error initializing client: " + ex.Message);
        }

        session.StartCoroutine(session.Co_ConnectToServer());
        session.StartCoroutine(session.Co_LaunchGame());
    }

    private IEnumerator Co_ConnectToServer()
    {
        try
        {
            Debug.Log("Attempting to connect to server at " + serverEndpointTCP);
            tcpClient.Connect(serverEndpointTCP);
            Debug.Log("Connected to server via TCP!");

            // Now the client is ready to send/receive UDP packages
            udpReady = true;
        }
        catch (Exception ex)
        {
            Debug.LogError("Error connecting to server: " + ex.Message);
        }
        yield return null;
    }

    private static IPEndPoint GetIPEndPoint(string hostName, int port)
    {
        var addresses = Dns.GetHostAddresses(hostName);
        var ip = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
        return new IPEndPoint(ip, port);
    }

    private static GameSession CreateNew()
    {
        var go = new GameObject("GameSession");
        DontDestroyOnLoad(go);
        return go.AddComponent<GameSession>();
    }

    private static PlayerController SpawnPlayer()
    {
        var prefab = Resources.Load<PlayerController>("Player");
        Debug.Log("Player Spawned");
        return Instantiate(prefab);
    }

    private static OpponentController SpawnOpponent()
    {
        var prefab = Resources.Load<OpponentController>("Opponent");
        Debug.Log("Opponent Spawned");
        return Instantiate(prefab);
    }

    [Serializable]
    private class PlayerState
    {
        public Vector3 position;
        public float size;

        public PlayerState(Vector3 pos, float sz)
        {
            position = pos;
            size = sz;
        }
    }
}
