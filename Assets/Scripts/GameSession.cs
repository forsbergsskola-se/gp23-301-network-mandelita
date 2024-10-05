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
    #endregion

 
    private async void FixedUpdate()
    {
        if (!finishedLoading) return;

        if (!isServer)
        {
            if (IsPlayerInitialized())
            {
                await SendPositionToServer();
                await ReceiveOpponentUpdates();
            }
        }
        else
        {
            if (AreOpponentsInitialized())
            {
                await ReceivePositions();
            }
        }
    }

    private bool IsPlayerInitialized()
    {
        return playerController != null && playerController.GetComponent<Blob>() != null;
    }

    private bool AreOpponentsInitialized()
    {
        // Ensure that the opponents dictionary is populated and valid.
        return opponents.Count > 0 && opponents.All(o => o.Value != null);
    }

    private void EnsureOpponentAndUpdatePosition(IPEndPoint opponentEndpoint, Vector3 position, float size)
    {
        if (!opponents.TryGetValue(opponentEndpoint, out var opponentController))
        {
            opponentController = SpawnOpponent();
            opponents[opponentEndpoint] = opponentController;
        }

        if (opponentController != null)
        {
            Debug.Log($"Updating opponent position for {opponentEndpoint}: {position}, size: {size}");
            opponentController.UpdatePosition(position, size);
        }
    }


    private async Task SendPositionToServer()
    {
        var position = playerController.transform.position;
        var size = playerController.GetComponent<Blob>().Size;

        var state = new PlayerState(position, size);
        var stateJson = JsonUtility.ToJson(state);
        var bytes = Encoding.UTF8.GetBytes(stateJson);

        Debug.Log("Client sending update.."); //YES

        try
        {
            // Send player position to the server via UDP
            await udpClient.SendAsync(bytes, bytes.Length, serverEndpointUDP);
            Debug.Log($"Client sent!"); //?
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error sending UDP packet: {ex.Message}"); //N
        }
    }


    private void StartReceivingPositions()
    {
        Task.Run(async () =>
        {
            Debug.Log("Server listening for positions..."); // YES

            while (true)
            {
                try
                {
                    var receiveResult = await udpClient.ReceiveAsync();
                    var fromEndpoint = receiveResult.RemoteEndPoint;
                    var receivedBytes = receiveResult.Buffer;
                    var receivedJson = Encoding.UTF8.GetString(receivedBytes);

                    var playerState = JsonUtility.FromJson<PlayerState>(receivedJson);
                    Debug.Log($"Received position from {fromEndpoint}: {playerState.position}"); //NO

                    EnsureOpponentAndUpdatePosition(fromEndpoint, playerState.position, playerState.size);
                    BroadcastOpponentStates(); 
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error receiving UDP packets: {ex.Message}");
                }
            }
        });
    }

    
    private async Task ReceivePositions()
    {
        Debug.Log("Server listening for positions..."); //YES
    
        while (true) 
        {
            try
            {
                var receiveResult = await udpClient.ReceiveAsync(); 
                var fromEndpoint = receiveResult.RemoteEndPoint;
                var receivedBytes = receiveResult.Buffer;
                var receivedJson = Encoding.UTF8.GetString(receivedBytes);

                var playerState = JsonUtility.FromJson<PlayerState>(receivedJson);
                Debug.Log($"Received position from {fromEndpoint}"); //NO

                EnsureOpponentAndUpdatePosition(fromEndpoint, playerState.position, playerState.size);
                BroadcastOpponentStates(); 
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error receiving UDP packets: {ex.Message}"); //NO
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
            
            Debug.Log("Broadcasting to clients: " + stateJson); // NO
            
            foreach (var client in clients)
            {
                udpClient.SendAsync(bytes, bytes.Length, client);
            }
        }
    }
    
    private async Task ReceiveOpponentUpdates()
    {
        try
        {
            var receiveResult = await udpClient.ReceiveAsync(); // Blocks until data is received
            var receivedBytes = receiveResult.Buffer;
            var receivedJson = Encoding.UTF8.GetString(receivedBytes);
            var opponentState = JsonUtility.FromJson<PlayerState>(receivedJson);

            Debug.Log($"Received opponent update");

            EnsureOpponentAndUpdatePosition(receiveResult.RemoteEndPoint, opponentState.position, opponentState.size);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error receiving UDP packets: {ex.Message}");
        }
    }

    
    public static void HostGame()
    {
        try
        {
            var session = CreateNew();
            session.isServer = true;

            session.udpClient = new UdpClient(UDPPortNumber); 
            Debug.Log("UDP Listener started on port 50000"); //YES

            session.tcpListener = new TcpListener(IPAddress.Any, TcpPortNumber); 
            session.tcpListener.Start();
            Debug.Log("TCP Listener started on port " + TcpPortNumber); //YES

            session.StartCoroutine(session.Co_AcceptClients());
            session.StartCoroutine(session.Co_LaunchGame());
            session.StartReceivingPositions();

            Debug.Log("HostGame successfully started"); //YES
        }
        catch (Exception ex)
        {
            Debug.LogError("Error in HostGame: " + ex.Message);
        }
    }
    
  // Accepts clients via TCP
    private IEnumerator Co_AcceptClients()
    {
        Debug.Log("Waiting for TCP clients to connect..."); // YES

        while (true)
        {
            var task = tcpListener.AcceptTcpClientAsync();
            while (!task.IsCompleted)
            {
                yield return null; 
            }

            var client = task.Result;
            Debug.Log("Client connected via TCP!"); // YES

            var clientEndpoint = (IPEndPoint)client.Client.RemoteEndPoint;
            clients.Add(clientEndpoint); 

            var newOpponent = SpawnOpponent();
            opponents[clientEndpoint] = newOpponent;

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
            Debug.Log("UDP client initialized"); //YES

            session.tcpClient = new TcpClient();
            session.serverEndpointTCP = GetIPEndPoint(hostName, TcpPortNumber);
            Debug.Log("TCP client initialized, server endpoint: " + session.serverEndpointTCP); //YES
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
            Debug.Log("Attempting to connect to server at " + serverEndpointTCP); // YES
            tcpClient.Connect(serverEndpointTCP);
            Debug.Log("Connected to server via TCP!"); // YES
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
