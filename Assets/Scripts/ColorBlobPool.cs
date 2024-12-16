using System.Collections.Generic;
using UnityEngine;

public class ColorBlobPool : MonoBehaviour
{
    public GameObject blobPrefab;
    public int poolSize = 10;
    private Queue<GameObject> pool;

    void Start()
    {
        pool = new Queue<GameObject>();

        for (int i = 0; i < poolSize; i++)
        {
            GameObject blob = Instantiate(blobPrefab);
            blob.SetActive(false); 
            pool.Enqueue(blob);
        }
    }

    public GameObject GetBlob()
    {
        if (pool.Count > 0)
        {
            var blob = pool.Dequeue();
            blob.SetActive(true);
            return blob;
        }
        
        return Instantiate(blobPrefab);
    }

    public void ReturnBlob(GameObject blob)
    {
        blob.SetActive(false);
        pool.Enqueue(blob);
    }
}