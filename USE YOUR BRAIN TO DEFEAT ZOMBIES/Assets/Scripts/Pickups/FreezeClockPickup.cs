using UnityEngine;

public class FreezeClockPickup : SpawnedObject
{
    [Header("Zegar zamra¿aj¹cy")]
    [SerializeField] private float freezeDuration = 5.0f;

    protected override void OnPickup(GameObject player)
    {
        Zombie.FreezeAllZombies(freezeDuration);
    }
}