using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GameSession : MonoBehaviour
{
    private const int UDPPortNumber = 44445;
    private const int TcpPortNumber = 44444;
    private bool finishedLoading;
    private PlayerController playerController;
    public bool isServer;

    #region ------ Client -------
    private IPEndPoint serverEndpoint;
    private UdpClient udpClient;
    private TcpClient tcpClient;   
    #endregion

    #region ------ Server -------
    private Dictionary<IPEndPoint, OpponentController> opponents = new();
    private TcpListener tcpListener;
    #endregion
    
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
        return Instantiate(prefab);
    }

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

            session.StartCoroutine(session.Co_AcceptClients());  // Coroutine to accept clients via TCP
            session.StartCoroutine(session.Co_LaunchGame());     // Launch the game scene

            Debug.Log("HostGame successfully started");
        }
        catch (Exception ex)
        {
            Debug.LogError("Error in HostGame: " + ex.Message);
        }
    }
    
    private IEnumerator Co_AcceptClients()
    {
        while (true)
        {
            Debug.Log("Waiting for TCP clients to connect...");
        
            // Use async method to prevent blocking the main thread
            var task = tcpListener.AcceptTcpClientAsync();  // Non-blocking
            while (!task.IsCompleted)
            {
                yield return null;  // Yield until a client connects
            }
        
            var tcpClient = task.Result;  // Get the connected TCP client
            Debug.Log("Client connected via TCP!");

            var clientEndpoint = (IPEndPoint)tcpClient.Client.RemoteEndPoint;

            // Spawn a new opponent for the client
            var opponentController = SpawnOpponent();
        
            // Add to opponents dictionary if not already present
            if (!opponents.ContainsKey(clientEndpoint))
            {
                opponents.Add(clientEndpoint, opponentController);
            }
        
            yield return null;
        }
    }
    
    private IEnumerator Co_LaunchGame()
    {
        yield return SceneManager.LoadSceneAsync("Game");
        playerController = SpawnPlayer();
        finishedLoading = true;
    }
    
   
    

    private async void FixedUpdate()
    {
        if (!finishedLoading) return;
        if (isServer)
            await ReceivePositions();
        else
            await SendPositionToServer();
    }
    
    private async Task ReceivePositions()
    {
        try
        {
            while (udpClient.Available > 0)
            {
                var receiveResult = await udpClient.ReceiveAsync();
                var fromEndpoint = receiveResult.RemoteEndPoint;
                var bytes = receiveResult.Buffer;
                var chars = Encoding.UTF8.GetString(bytes);

                var state = JsonUtility.FromJson<PlayerState>(chars);  // Deserialize the received player state

                // Update opponent's position and size
                EnsureOpponentAndUpdatePosition(fromEndpoint, state.Position, state.Size);

                // Broadcast the updated state of all opponents to all clients
                BroadcastOpponentStates();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Error receiving positions: " + ex.Message);
        }
    }

    
    private void EnsureOpponentAndUpdatePosition(IPEndPoint opponentEndpoint, Vector3 opponentPosition, float opponentSize)
    {
        if (!opponents.TryGetValue(opponentEndpoint, out var opponentController))
        {
            opponentController = SpawnOpponent();
            opponents[opponentEndpoint] = opponentController;
        }
        
        opponentController.transform.position = opponentPosition;
        opponentController.GetComponent<Blob>().Size = opponentSize;  
    }
    
    private void BroadcastOpponentStates()
    {
        foreach (var opponent in opponents)
        {
            var state = new PlayerState(opponent.Value.transform.position, opponent.Value.GetComponent<Blob>().Size);
        
            var chars = JsonUtility.ToJson(state);  // Serialize the state
            var bytes = Encoding.UTF8.GetBytes(chars);

            // Broadcast to all connected clients
            foreach (var endpoint in opponents.Keys)
            {
                udpClient.SendAsync(bytes, bytes.Length, endpoint);  // Send updated state to each opponent
            }
        }
    }
    
    private async Task SendPositionToServer()
    {
        try
        {
            var position = playerController.transform.position;
            var size = playerController.GetComponent<Blob>().Size;

            var state = new PlayerState(position, size);

            var chars = JsonUtility.ToJson(state);  // Serialize player state
            var bytes = Encoding.UTF8.GetBytes(chars);

            await udpClient.SendAsync(bytes, bytes.Length, serverEndpoint);
            Debug.Log("Position sent to server");
        }
        catch (Exception ex)
        {
            Debug.LogError("Error sending position to server: " + ex.Message);
        }
    }

    
    public void SendUpdatedStateToClients()
    {
        var position = playerController.transform.position;
        var size = playerController.GetComponent<Blob>().Size;

        var state = new PlayerState(position, size);

        var chars = JsonUtility.ToJson(state);  // Serialize updated state
        var bytes = Encoding.UTF8.GetBytes(chars);
    
        // Broadcast to all connected clients (you will need to handle this properly)
        foreach (var opponent in opponents.Keys)
        {
            udpClient.SendAsync(bytes, bytes.Length, opponent);  // Send updated state to each opponent
        }
    }
    


   
    
   
    
    
    // Join game as a client using TCP connection
    public static void JoinGame(string hostName)
    {
        var session = CreateNew();
        session.isServer = false;
        try
        {
            session.udpClient = new UdpClient();  // Initialize UDP without binding to a specific port
            Debug.Log("UDP client initialized without specific port");

        }
        catch (Exception ex)
        {
            Debug.LogError("Error initializing UDP client: " + ex.Message);
        }
        
        try
        {
            session.tcpClient = new TcpClient();
            session.serverEndpoint = GetIPEndPoint(hostName, TcpPortNumber);
            Debug.Log("TCP client initialized, server endpoint: " + session.serverEndpoint);
        }
        catch (Exception ex)
        {
            Debug.LogError("Error initializing TCP client or resolving server endpoint: " + ex.Message);
        }
        session.StartCoroutine(session.Co_ConnectToServer(hostName));  // Connect via TCP
        session.StartCoroutine(session.Co_LaunchGame());

    }

    private IEnumerator Co_ConnectToServer(string hostName)
    {
        try
        {
            var ipEndPoint = GetIPEndPoint(hostName, TcpPortNumber);
            tcpClient.Connect(ipEndPoint);  // Connect to the server via TCP
            Debug.Log("Connected to server via TCP!");

            // After connecting, send some initial data to the server (e.g., player info)
            var playerInfo = new PlayerState(playerController.transform.position, 1);
            var jsonData = JsonUtility.ToJson(playerInfo);
            var bytes = Encoding.UTF8.GetBytes(jsonData);

            var stream = tcpClient.GetStream();  // Get the network stream to send data
            stream.Write(bytes, 0, bytes.Length);  // Send data via TCP (no async needed in a coroutine)
            Debug.Log("Initial data sent to server");
        }
        catch (Exception ex)
        {
            Debug.LogError("Error connecting to server: " + ex.Message);
        }

        yield return null;
    }
    
   

    private static IPEndPoint GetIPEndPoint(string hostName, int port)
    {
        try
        {
            var address = Dns.GetHostAddresses(hostName).First();
            Debug.Log("Resolved IP Address for " + hostName + ": " + address);
            return new IPEndPoint(address, port);
        }
        catch (Exception ex)
        {
            Debug.LogError("Error resolving IP address for " + hostName + ": " + ex.Message);
            throw;
        }
    }

}


[Serializable]
public class PlayerState
{
    public Vector3 Position;
    public float Size;

    public PlayerState(Vector3 position, float size)
    {
        Position = position;
        Size = size;
    }
}
