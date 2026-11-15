using UnityEngine;

public class DoubleAmmoPickup : SpawnedObject
{
    [Header("Podwójne naboje")]
    [SerializeField] private float effectDuration = 10.0f;

    protected override void OnPickup(GameObject player)
    {
        if (player.TryGetComponent(out PlayerController controller))
        {
            controller.ActivateDoubleShot(effectDuration);
        }
    }
}