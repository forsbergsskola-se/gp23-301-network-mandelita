using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
    private static string hostIP;
    #endregion

    private bool isCoroutineStarted = false;

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

    private void FixedUpdate()
    {
        if (!finishedLoading || isCoroutineStarted) return;

        // Start sending and receiving positions
        StartCoroutine(SendAndReceivePositions());
        isCoroutineStarted = true;
    }

    // Handles both sending and receiving player positions
    private IEnumerator SendAndReceivePositions()
    {
        if (isServer)
        {
            StartCoroutine(ReceivePositionsFromClients());
            StartCoroutine(BroadcastPlayerStatesToClients());
        }
        else
        {
            StartCoroutine(SendPositionToServer());
            StartCoroutine(ReceivePositionsFromServer());
        }
        yield return null;
    }

    private IEnumerator ReceivePositionsFromClients()
    {
        var udpEndPoint = new IPEndPoint(IPAddress.Any, UDPPortNumber);

        while (true)
        {
            try
            {
                var receiveResult = udpClient.Receive(ref udpEndPoint);
                var receivedData = Encoding.UTF8.GetString(receiveResult);

                if (string.IsNullOrEmpty(receivedData))
                {
                    Debug.LogWarning("Received empty data from: " + udpEndPoint);
                    continue;
                }

                var state = JsonUtility.FromJson<PlayerState>(receivedData);
                Debug.Log($"Received data from: {udpEndPoint} - Position: {state.position}, Size: {state.size}");

                EnsurePlayerAndUpdatePosition(udpEndPoint, state.position, state.size);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
            {
                Debug.Log("Socket error would block, delay ");
            }
            catch (Exception ex)
            {
                Debug.LogError("Error receiving positions: " + ex.Message);
            }
            yield return null;
        }
    }

    private void EnsurePlayerAndUpdatePosition(IPEndPoint fromEndpoint, Vector3 statePosition, float stateSize)
    {
        if (!opponents.TryGetValue(fromEndpoint, out var opponentController))
        {
            opponentController = SpawnOpponent(); // Spawn opponent if not found
            opponents[fromEndpoint] = opponentController;
        }

        opponentController.transform.position = statePosition;
        opponentController.GetComponent<Blob>().Size = stateSize;
    }

    private IEnumerator BroadcastPlayerStatesToClients()
    {
        while (true)
        {
            try
            {
                var state = new PlayerState(playerController.transform.position, playerController.GetComponent<Blob>().Size);
                var json = JsonUtility.ToJson(state);
                var bytes = Encoding.UTF8.GetBytes(json);

                foreach (var client in clients)
                {
                    udpClient.SendAsync(bytes, bytes.Length, client);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("Error broadcasting player states to clients: " + ex.Message);
            }
            yield return new WaitForSeconds(0.1f); // Send player state every 0.1 seconds
        }
    }

    private IEnumerator SendPositionToServer()
    {
        while (true)
        {
            if (playerController == null) yield return null;

            try
            {
                var state = new PlayerState(playerController.transform.position, playerController.GetComponent<Blob>().Size);
                var json = JsonUtility.ToJson(state);
                var bytes = Encoding.UTF8.GetBytes(json);

                udpClient.SendAsync(bytes, bytes.Length, serverEndpoint);
            }
            catch (Exception ex)
            {
                Debug.LogError("Error sending position to server: " + ex.Message);
            }
            yield return new WaitForSeconds(0.1f); // Send position every 0.1 seconds
        }
    }

    private IEnumerator ReceivePositionsFromServer()
    {
        var serverEndPoint = new IPEndPoint(IPAddress.Parse(hostIP), UDPPortNumber);

        while (true)
        {
            try
            {
                var receiveResult = udpClient.Receive(ref serverEndPoint);
                var receivedData = Encoding.UTF8.GetString(receiveResult);

                if (string.IsNullOrEmpty(receivedData))
                {
                    Debug.LogWarning("Received empty data from server: " + serverEndPoint);
                    continue;
                }

                var state = JsonUtility.FromJson<PlayerState>(receivedData);
                Debug.Log($"Received data from server: {serverEndPoint} - Position: {state.position}, Size: {state.size}");

                EnsureOpponentAndUpdatePosition(state.position, state.size);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
            {
                Debug.Log("Socket error would block, delay ");
            }
            catch (Exception ex)
            {
                Debug.LogError("Error receiving positions from server: " + ex.Message);
            }
            yield return null;
        }
    }

    private void EnsureOpponentAndUpdatePosition(Vector3 position, float size)
    {
        if (opponentController == null)
        {
            opponentController = SpawnOpponent();
        }

        opponentController.transform.position = position;
        opponentController.GetComponent<Blob>().Size = size;
    }

    // Host game setup
    public static void HostGame()
    {
        try
        {
            var session = CreateNew();  // Creates the GameSession object
            session.isServer = true;

            session.udpClient = new UdpClient(UDPPortNumber);  // Initialize UDP listener
            Debug.Log("UDP Listener started on port " + UDPPortNumber);

            session.tcpListener = new TcpListener(IPAddress.Any, TcpPortNumber);  // Initialize TCP listener
            session.tcpListener.Start();  // Start listening for TCP clients
            Debug.Log("TCP Listener started on port " + TcpPortNumber);

            session.StartCoroutine(session.Co_AcceptClients());  // Accept clients via TCP
            session.StartCoroutine(session.Co_LaunchGame());     // Launch the game scene

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
        GameSession.hostIP = hostName;

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
