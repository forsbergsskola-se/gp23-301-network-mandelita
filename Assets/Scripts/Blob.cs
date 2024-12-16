
using System;
using System.Net;
using UnityEngine;

public class Blob : MonoBehaviour
{
    public Vector2 Direction { get; set; }
    private float size = 1f;
    public float Size
    {
        get => size;
        set
        {
            transform.localScale = new Vector3(value, value, 1f);
            size = value;
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
            if (otherBlob != null && otherBlob.Size < Size)  
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
        Size += opponent.Size; 
        opponent.Size = 1f; 
        
        opponent.transform.position = new Vector3(0f, 0f, 0f);
        opponent.GetComponent<Blob>().Size = 1f; 
    }
}
