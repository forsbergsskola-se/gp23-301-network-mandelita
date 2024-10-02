using UnityEngine;

[RequireComponent(typeof(Blob))]
public class OpponentController : MonoBehaviour
{
    private Blob _blob;
    private void Start()
    {
        _blob = GetComponent<Blob>();
    }
    
}