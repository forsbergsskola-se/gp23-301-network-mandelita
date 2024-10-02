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
            //BroadcastOpponentStates();
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
        var position = playerController.transform.position;
        var size = playerController.GetComponent<Blob>().Size;

        var state = new PlayerState(position, size);

        var chars = JsonUtility.ToJson(state);  // Serialize player state
        var bytes = Encoding.UTF8.GetBytes(chars);
    
        await udpClient.SendAsync(bytes, bytes.Length, serverEndpoint);
    }

    /*
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
    */


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

    // Host game with TCP listener for new connections
    public static void HostGame()
    {
        var session = CreateNew();
        session.isServer = true;
        session.udpClient = new UdpClient(UDPPortNumber);
        session.tcpListener = new TcpListener(IPAddress.Any, TcpPortNumber);  // Start TCP listener
        session.tcpListener.Start();
        session.StartCoroutine(session.Co_AcceptClients());  // Accept clients via TCP
        session.StartCoroutine(session.Co_LaunchGame());
    }

    // Coroutine to accept incoming TCP client connections
    private IEnumerator Co_AcceptClients()
    {
        while (true)
        {
            // Accept a new TCP client connection
            var tcpClient = tcpListener.AcceptTcpClient();  // Block and wait for clients to connect
            Debug.Log("Client connected via TCP!");
        
            // Get the client's IP endpoint (used as the key in the dictionary)
            var clientEndpoint = (IPEndPoint)tcpClient.Client.RemoteEndPoint;

            // Spawn a new opponent in the game world
            var opponentController = SpawnOpponent();  // Assuming you have a method to spawn opponents
        
            // Add the opponent to the dictionary, using their endpoint as the key
            if (!opponents.ContainsKey(clientEndpoint))
            {
                opponents.Add(clientEndpoint, opponentController);  // Add the new opponent
            }
            else
            {
                Debug.LogWarning("Client already connected!");
            }
            yield return null;
        }
    }


    // Join game as a client using TCP connection
    public static void JoinGame(string hostName)
    {
        var session = CreateNew();
        session.isServer = false;
        session.udpClient = new UdpClient();
        session.tcpClient = new TcpClient();  // Initialize TCP client
        session.serverEndpoint = GetIPEndPoint(hostName, UDPPortNumber);
        session.StartCoroutine(session.Co_ConnectToServer(hostName));  // Connect via TCP
        session.StartCoroutine(session.Co_LaunchGame());
    }

    // Coroutine to connect to server via TCP
    private IEnumerator Co_ConnectToServer(string hostName)
    {
        var ipEndPoint = GetIPEndPoint(hostName, TcpPortNumber);
        tcpClient.Connect(ipEndPoint);  // Connect to the server via TCP
        Debug.Log("Connected to server via TCP!");
        // Handle further logic after connection is established
        yield return null;
    }

    private IEnumerator Co_LaunchGame()
    {
        yield return SceneManager.LoadSceneAsync("Game");
        playerController = SpawnPlayer();
        finishedLoading = true;
    }

    private static IPEndPoint GetIPEndPoint(string hostName, int port)
    {
        var address = Dns.GetHostAddresses(hostName).First();
        return new IPEndPoint(address, port);
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
