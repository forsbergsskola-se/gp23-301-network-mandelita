using UnityEngine;

public class ColorBlob : MonoBehaviour
{
    private void OnEnable()
    {
        GetComponent<Renderer>().material.color = Random.ColorHSV();
    }
    
    public void Consume()
    {
        gameObject.SetActive(false);
    }
}