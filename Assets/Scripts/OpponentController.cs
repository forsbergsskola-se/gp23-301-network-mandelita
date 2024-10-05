using UnityEngine;

[RequireComponent(typeof(Blob))]
public class OpponentController : MonoBehaviour
{
    private Blob _blob;

    private void Start()
    {
        _blob = GetComponent<Blob>();
    }

    public void UpdatePosition(Vector3 newPosition, float newSize)
    {
        transform.position = newPosition;
        _blob.Size = newSize; // Update the blob size
    }
}