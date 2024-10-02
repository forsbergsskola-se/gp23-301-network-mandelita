
using UnityEngine;

public class Blob : MonoBehaviour
{
    public Vector2 Direction { get; set; }
    private float _size = 1f;
    public float Size
    {
        get => _size;
        set
        {
            transform.localScale = new Vector3(value, value, 1f);
            _size = value;
        }
    }

    public float baseSpeed = 3f;

    void Update()
    {
        GetComponent<Rigidbody2D>().velocity = Direction.normalized * baseSpeed / Size;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (other.CompareTag("ColorBlob"))
        {
            var colorBlob = other.GetComponent<ColorBlob>();
            if (colorBlob != null)
            {
                colorBlob.Consume();  
                Grow();               
            }
        }
        else if (other.CompareTag("Opponent"))
        {
            var otherBlob = other.GetComponent<Blob>();
            if (otherBlob != null && otherBlob.Size < Size)  // You can only eat smaller opponents
            {
                EatOpponent(otherBlob);
            }
        }
    }

    private void Grow()
    {
        Size += 0.1f;  
    }

    private void EatOpponent(Blob opponent)
    {
        Size += opponent.Size; // Increase the player's size by the size of the opponent eaten
        opponent.Size = 1f; // Reset the opponent's size
        // Optionally, you can handle opponent removal or respawning here.
    
        // Inform the GameSession to send updated state
        var gameSession = FindObjectOfType<GameSession>();
        if (gameSession != null && gameSession.isServer)
        {
            //gameSession.SendUpdatedStateToClients(); // Send the updated state to clients
        }   
    }
}
