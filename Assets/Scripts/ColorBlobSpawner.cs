using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ColorBlobSpawner : MonoBehaviour
{
    public ColorBlobPool blobPool;
    public Vector2 arenaBounds = new Vector2(10f, 10f); // X and Z bounds for the arena
    public float spawnInterval = 2f;

    void Start()
    {
        InvokeRepeating(nameof(SpawnBlob), 0f, spawnInterval);
    }

    private void SpawnBlob()
    {
        GameObject blob = blobPool.GetBlob();
        blob.transform.position = GetRandomPositionWithinBounds();
    }

    private Vector3 GetRandomPositionWithinBounds()
    {
        float x = Random.Range(-arenaBounds.x, arenaBounds.x);
        float y = Random.Range(-arenaBounds.y, arenaBounds.y);
        return new Vector3(x, y, 0f); 
    }
}

