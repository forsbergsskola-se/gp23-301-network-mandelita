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
        if (_blob != null)
        {
            transform.position = newPosition;
            _blob.Size = newSize;
        }
        else
        {
            Debug.LogWarning("Blob component is null, cannot update position or size.");
        }
    }

}