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

    private async void FixedUpdate()
    {
        if (!finishedLoading || !udpReady) return;

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
        if (!udpReady) return;

        var position = playerController.transform.position;
        var size = playerController.GetComponent<Blob>().Size;

        var state = new PlayerState(position, size);
        var stateJson = JsonUtility.ToJson(state);
        var bytes = Encoding.UTF8.GetBytes(stateJson);

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

    private async Task ReceivePositions()
    {
        while (true)
        {
            try
            {
                var receiveResult = await udpClient.ReceiveAsync();
                var fromEndpoint = receiveResult.RemoteEndPoint;

                // Skip processing if it's from the server's own UDP endpoint
                if (fromEndpoint.Equals(serverEndpointUDP)) continue;

                var receivedJson = Encoding.UTF8.GetString(receiveResult.Buffer);
                if (!IsValidJson(receivedJson)) continue;

                var playerState = JsonUtility.FromJson<PlayerState>(receivedJson);
                if (playerState == null) continue;

                // Update existing opponent
                if (opponents.TryGetValue(fromEndpoint, out var opponentController))
                {
                    opponentController.UpdatePosition(playerState.position, playerState.size);
                }
                else
                {
                    // Spawn a new opponent if it does not exist
                    Debug.Log($"Spawning new opponent for {fromEndpoint}");
                    opponents[fromEndpoint] = SpawnOpponent();
                    opponents[fromEndpoint].UpdatePosition(playerState.position, playerState.size);
                }

                BroadcastOpponentStates(); // Ensure the state is broadcasted
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error receiving UDP packets: {ex.Message}");
            }
            await Task.Yield();
        }
    }

    private async Task ReceiveOpponentUpdates()
    {
        try
        {
            var receiveResult = await udpClient.ReceiveAsync();
            var receivedJson = Encoding.UTF8.GetString(receiveResult.Buffer);

            if (!IsValidJson(receivedJson)) return;

            var opponentState = JsonUtility.FromJson<PlayerState>(receivedJson);
            var opponentEndpoint = receiveResult.RemoteEndPoint;

            // Spawn or update the opponent's position
            if (!opponents.ContainsKey(opponentEndpoint))
            {
                Debug.Log($"Spawning new opponent on client for {opponentEndpoint}");
                opponents[opponentEndpoint] = SpawnOpponent();
            }

            opponents[opponentEndpoint]?.UpdatePosition(opponentState.position, opponentState.size);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error receiving opponent update: {ex.Message}");
        }

        await Task.Yield();
    }

    private void BroadcastOpponentStates()
    {
        foreach (var opponent in opponents)
        {
            if (opponent.Value == null) continue; // Ensure the opponent is still valid

            var state = new PlayerState(opponent.Value.transform.position, opponent.Value.GetComponent<Blob>().Size);
            var stateJson = JsonUtility.ToJson(state);
            var bytes = Encoding.UTF8.GetBytes(stateJson);

            foreach (var client in clients)
            {
                if (client != null && !client.Equals(serverEndpointUDP))
                {
                    udpClient.SendAsync(bytes, bytes.Length, client);
                }
            }
        }
    }

    public static void HostGame()
    {
        var session = CreateNew();
        session.isServer = true;
        session.udpClient = new UdpClient(UDPPortNumber);
        session.tcpListener = new TcpListener(IPAddress.Any, TcpPortNumber);
        session.tcpListener.Start();
        session.StartCoroutine(session.Co_AcceptClients());
        session.StartCoroutine(session.Co_LaunchGame());
    }

    private IEnumerator Co_AcceptClients()
    {
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

            if (!clients.Contains(clientEndpoint))
            {
                clients.Add(clientEndpoint);

                // Send host state to the newly connected client
                SendHostStateToClient(clientEndpoint);
            }

            yield return null;
        }
    }

    private void SendHostStateToClient(IPEndPoint clientEndpoint)
    {
        var hostState = new PlayerState(playerController.transform.position, playerController.GetComponent<Blob>().Size);
        var stateJson = JsonUtility.ToJson(hostState);
        var bytes = Encoding.UTF8.GetBytes(stateJson);

        udpClient.SendAsync(bytes, bytes.Length, clientEndpoint);
        Debug.Log($"Sent host state to {clientEndpoint}: {stateJson}");
    }

    private IEnumerator Co_LaunchGame()
    {
        yield return SceneManager.LoadSceneAsync("Game");
        playerController = SpawnPlayer();
        finishedLoading = true;
        udpReady = true; // Ensure UDP is ready after loading the scene
    }

    public static void JoinGame(string hostName)
    {
        var session = CreateNew();
        session.isServer = false;

        session.udpClient = new UdpClient();
        session.serverEndpointUDP = GetIPEndPoint(hostName, UDPPortNumber);
        session.tcpClient = new TcpClient();
        session.serverEndpointTCP = GetIPEndPoint(hostName, TcpPortNumber);
        session.StartCoroutine(session.Co_ConnectToServer());
        session.StartCoroutine(session.Co_LaunchGame());
    }

    private IEnumerator Co_ConnectToServer()
    {
        try
        {
            tcpClient.Connect(serverEndpointTCP);
            udpReady = true; // Mark UDP as ready after successful TCP connection
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
        return Instantiate(prefab);
    }

    private static OpponentController SpawnOpponent()
    {
        var prefab = Resources.Load<OpponentController>("Opponent");
        return Instantiate(prefab);
    }

    private bool IsValidJson(string json)
    {
        json = json.Trim();
        if ((json.StartsWith("{") && json.EndsWith("}")) || (json.StartsWith("[") && json.EndsWith("]")))
        {
            try
            {
                var obj = JsonUtility.FromJson<PlayerState>(json);
                return obj != null;
            }
            catch
            {
                return false;
            }
        }
        return false;
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
