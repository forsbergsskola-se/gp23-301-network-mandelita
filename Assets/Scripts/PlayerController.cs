using UnityEngine;

[RequireComponent(typeof(Blob))]
public class PlayerController : MonoBehaviour
{
    private bool isServer;
    private Blob blob;
    
    private void Start()
    {
        blob = GetComponent<Blob>();
    }
    
    void Update()
    {
        var cursorInWorld = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        blob.Direction = cursorInWorld - transform.position;
    }
    
    void LateUpdate()
    {
        Vector3 position = transform.position;

        // Arena size, adjust accordingly
        float arenaMinX = -10f, arenaMaxX = 10f;
        float arenaMinY = -10f, arenaMaxY = 10f;

        // Clamp the player's position within arena bounds
        position.x = Mathf.Clamp(position.x, arenaMinX, arenaMaxX);
        position.y = Mathf.Clamp(position.y, arenaMinY, arenaMaxY);

        // Update camera position to follow the player within bounds
        Camera.main.transform.position = new Vector3(position.x, position.y, Camera.main.transform.position.z);
    }
}
