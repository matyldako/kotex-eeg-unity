using UnityEngine;

public class MedkitPickup : SpawnedObject
{
    [Header("Apteczka")]
    [SerializeField] private int healAmount = 1;

    protected override void OnPickup(GameObject player)
    {
        if (player.TryGetComponent(out PlayerHealth health))
        {
            health.Heal(healAmount);
        }
    }
}