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
    private const int UDPPortNumber = 44445;
    private const int TcpPortNumber = 44446;
    private bool finishedLoading;
    private PlayerController playerController;
    private OpponentController opponentController;
    public bool isServer;

    #region ------ Client -------
    private IPEndPoint serverEndpoint;
    private UdpClient udpClient;
    private TcpClient tcpClient;
    #endregion

    #region ------ Server -------
    private Dictionary<IPEndPoint, OpponentController> opponents = new();
    private List<IPEndPoint> clients = new();
    private TcpListener tcpListener;
    #endregion

 
    private async void FixedUpdate()
    {
        if (!finishedLoading) return;

        if (!isServer)
        {
            await SendPositionToServer();
            await ReceiveOpponentUpdates();
        }
        else
        {
            await ReceivePositions();
        }
        
            
    }

    private async Task SendPositionToServer()
    {
        var position = playerController.transform.position;
        var size = playerController.GetComponent<Blob>().Size;

        var state = new PlayerState(position, size);
        var stateJson = JsonUtility.ToJson(state);
        var bytes = Encoding.UTF8.GetBytes(stateJson);
    
        Debug.Log($"Client sending update: {stateJson}"); // Log the JSON being sent

        // Send player position to server via UDP
        await udpClient.SendAsync(bytes, bytes.Length, serverEndpoint);
    }

    
    private async Task ReceivePositions()
    {
        Debug.Log("Server listening for positions...");
    
        while (true) // Keep listening for incoming packets
        {
            try
            {
                var receiveResult = await udpClient.ReceiveAsync(); // Await incoming packets
                var fromEndpoint = receiveResult.RemoteEndPoint;
                var receivedBytes = receiveResult.Buffer;
                var receivedJson = Encoding.UTF8.GetString(receivedBytes);

                var playerState = JsonUtility.FromJson<PlayerState>(receivedJson);
                Debug.Log($"Received position from {fromEndpoint}");

                EnsureOpponentAndUpdatePosition(fromEndpoint, playerState.position, playerState.size);
                BroadcastOpponentStates(); // Ensure this works as expected
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error receiving UDP packets: {ex.Message}");
            }
        }
    }


    private void BroadcastOpponentStates()
    {
        foreach (var opponent in opponents)
        {
            var state = new PlayerState(opponent.Value.transform.position, opponent.Value.GetComponent<Blob>().Size);
            var stateJson = JsonUtility.ToJson(state);
            var bytes = Encoding.UTF8.GetBytes(stateJson);
            
            Debug.Log("Broadcasting to clients: " + stateJson);

            // Send the updated state to all connected clients
            foreach (var client in clients)
            {
                udpClient.SendAsync(bytes, bytes.Length, client);
            }
        }
    }

    private void EnsureOpponentAndUpdatePosition(IPEndPoint opponentEndpoint, Vector3 position, float size)
    {
        if (!opponents.TryGetValue(opponentEndpoint, out var opponentController))
        {
            opponentController = SpawnOpponent();
            opponents[opponentEndpoint] = opponentController;
        }

        Debug.Log($"Updating opponent position for {opponentEndpoint}: {position}, size: {size}");
        opponentController.transform.position = position;
        opponentController.GetComponent<Blob>().Size = size;
    }

    private async Task ReceiveOpponentUpdates()
    {
        while (udpClient.Available > 0)
        {
            var receiveResult = await udpClient.ReceiveAsync();
            var receivedBytes = receiveResult.Buffer;
            var receivedJson = Encoding.UTF8.GetString(receivedBytes);
 
            var opponentState = JsonUtility.FromJson<PlayerState>(receivedJson);

            Debug.Log($"Received opponent update: {opponentState.position}, size: {opponentState.size}");

            if (opponentController != null)
            {
                opponentController.transform.position = opponentState.position;
                opponentController.GetComponent<Blob>().Size = opponentState.size;
            }
        }
    }


    // Host game setup
    public static void HostGame()
    {
        try
        {
            var session = CreateNew(); 
            session.isServer = true;

            session.udpClient = new UdpClient(UDPPortNumber); 
            Debug.Log("UDP Listener started on port " + UDPPortNumber);

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

    // Accepts clients via TCP
    private IEnumerator Co_AcceptClients()
    {
        Debug.Log("Waiting for TCP clients to connect...");

        while (true)
        {
            var task = tcpListener.AcceptTcpClientAsync();
            while (!task.IsCompleted)
            {
                yield return null; // Wait for connection
            }

            var client = task.Result;
            Debug.Log("Client connected via TCP!");

            var clientEndpoint = (IPEndPoint)client.Client.RemoteEndPoint;
            clients.Add(clientEndpoint); // Add client end point to the list

            var opponentController = SpawnOpponent();
            opponents[clientEndpoint] = opponentController;

            yield return null;
        }
    }

    // Launches the game scene
    private IEnumerator Co_LaunchGame()
    {
        yield return SceneManager.LoadSceneAsync("Game");
        playerController = SpawnPlayer();
        finishedLoading = true;
    }

    // Client joins game and connects to server
    public static void JoinGame(string hostName)
    {
        var session = CreateNew();
        session.isServer = false;

        try
        {
            session.udpClient = new UdpClient();
            Debug.Log("UDP client initialized");

            session.tcpClient = new TcpClient();
            session.serverEndpoint = GetIPEndPoint(hostName, TcpPortNumber);
            Debug.Log("TCP client initialized, server endpoint: " + session.serverEndpoint);
        }
        catch (Exception ex)
        {
            Debug.LogError("Error initializing client: " + ex.Message);
        }

        session.StartCoroutine(session.Co_ConnectToServer());
        session.StartCoroutine(session.Co_LaunchGame());
    }

    // Connects client to server
    private IEnumerator Co_ConnectToServer()
    {
        try
        {
            Debug.Log("Attempting to connect to server at " + serverEndpoint);
            tcpClient.Connect(serverEndpoint);
            Debug.Log("Connected to server via TCP!");
        }
        catch (Exception ex)
        {
            Debug.LogError("Error connecting to server: " + ex.Message);
        }
        yield return null;
    }

    // Helper to get IP from host name
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


    // Data structure for player state
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
