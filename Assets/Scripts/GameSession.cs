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
    private bool isConnectedToServer;
    #endregion

    #region ------ Server -------
    private Dictionary<IPEndPoint, OpponentController> opponents = new();
    private List<IPEndPoint> clients = new();
    private TcpListener tcpListener;
    #endregion

 
    private void FixedUpdate()
    {
        if (!finishedLoading || !isConnectedToServer) return; 

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

    private void SendPositionToServer()
    {
        var position = playerController.transform.position;
        var size = playerController.GetComponent<Blob>().Size;

        var state = new PlayerState(position, size);
        var stateJson = JsonUtility.ToJson(state);
        var bytes = Encoding.UTF8.GetBytes(stateJson);

        Debug.Log("Client sending update..");

        try
        {
            // Send player position to the server via UDP
            udpClient.Send(bytes, bytes.Length, serverEndpointUDP);
            Debug.Log($"Client sent!");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error sending UDP packet: {ex.Message}");
        }
    }

    private void ReceivePositions()
    {
        try
        {
            if (udpClient.Available > 0)
            {
                var receiveResult = udpClient.Receive(ref serverEndpointUDP);
                var receivedJson = Encoding.UTF8.GetString(receiveResult);

                var playerState = JsonUtility.FromJson<PlayerState>(receivedJson);
                Debug.Log($"Server received position");

                EnsureOpponentAndUpdatePosition(serverEndpointUDP, playerState.position, playerState.size);
                BroadcastOpponentStates();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error receiving UDP packets: {ex.Message}");
        }
    }

    private void BroadcastOpponentStates()
    {
        foreach (var opponent in opponents)
        {
            var state = new PlayerState(opponent.Value.transform.position, opponent.Value.GetComponent<Blob>().Size);
            var stateJson = JsonUtility.ToJson(state);
            var bytes = Encoding.UTF8.GetBytes(stateJson);

            Debug.Log("Broadcasting to clients...");

            foreach (var client in clients)
            {
                udpClient.Send(bytes, bytes.Length, client);
            }
        }
    }

    private void ReceiveOpponentUpdates()
    {
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
            opponentController = SpawnOpponent();
            opponents[opponentEndpoint] = opponentController;
        }

        if (opponentController != null)
        {
            Debug.Log($"Updating opponent position");
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
            Debug.Log("Client connected via TCP!");

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
            isConnectedToServer = true; 
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
