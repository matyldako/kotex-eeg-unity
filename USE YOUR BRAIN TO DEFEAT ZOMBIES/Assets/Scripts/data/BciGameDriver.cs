using UnityEngine;

public class BciGameDriver : MonoBehaviour
{
    [Header("Źródło parametrów synchronii")]
    [SerializeField] private BciParameters parameters;
    [SerializeField] private bool logDebugData = false;

    [Header("Progi synchronii dwóch mózgów (0..1)")]
    [Tooltip("Powyżej tej wartości synchronii (np. Alpha lub Combined) zombie zwalniają")]
    [SerializeField] private float highSyncThreshold = 0.65f;
    [Tooltip("Poniżej tej wartości synchronii zombie przyspieszają")]
    [SerializeField] private float lowSyncThreshold = 0.25f;
    [Tooltip("Próg synchronii Theta lub Gamma wyzwalający pojawienie się power-upu")]
    [SerializeField] private float criticalThetaSync = 0.65f;
    [SerializeField] private float criticalGammaSync = 0.65f;

    [Header("Modyfikatory prędkości Zombie")]
    [SerializeField] private float slowSpeedMultiplier = 0.6f;
    [SerializeField] private float fastSpeedMultiplier = 1.4f;

    [Header("Spawnowanie pomocy (Power-upy)")]
    [SerializeField] private float spawnCooldown = 13.0f;
    [Tooltip("Przypisz punkty na mapie, gdzie mogą pojawiać się przedmioty")]
    [SerializeField] private Transform[] spawnPoints;
    [Tooltip("Przypisz prefaby: Apteczka, Zegar, Podwójne Naboje")]
    [SerializeField] private GameObject[] pickupPrefabs;

    private float cooldownTimer;
    private bool isOnCooldown;

    public bool IsZombieSlowed { get; private set; }
    public bool IsZombieFast { get; private set; }

    private void Start()
    {
        if (parameters == null)
        {
            parameters = BciParameters.Instance;
        }
    }

    private void Update()
    {
        if (parameters == null) return;

        if (logDebugData)
        {
            Debug.Log($"HasSignal: {parameters.hasSignal}, Score: {parameters.score:F2}, " +
                      $"Combined: {parameters.combined:F2}, AlphaSync: {parameters.alpha:F2}, " +
                      $"ThetaSync: {parameters.theta:F2}, GammaSync: {parameters.gamma:F2}");
        }

        if (!parameters.hasSignal)
        {
            ResetZombieSpeed();
            return;
        }

        // Możesz użyć parameters.alpha (synchronia w paśmie Alpha)
        // lub parameters.combined (średnia synchronia z aktywnych pasm)
        float mainSync = parameters.alpha;

        UpdateZombieSpeedState(mainSync);
        UpdatePickupSpawning(parameters.theta, parameters.gamma);
    }

    private void UpdateZombieSpeedState(float syncValue)
    {
        if (syncValue >= highSyncThreshold)
        {
            // Gracze są zsynchronizowani -> zombie zwalniają
            IsZombieSlowed = true;
            IsZombieFast = false;
            Zombie.GlobalSpeedMultiplier = slowSpeedMultiplier;
        }
        else if (syncValue <= lowSyncThreshold)
        {
            // Brak synchronizacji między graczami -> zombie przyspieszają
            IsZombieSlowed = false;
            IsZombieFast = true;
            Zombie.GlobalSpeedMultiplier = fastSpeedMultiplier;
        }
        else
        {
            ResetZombieSpeed();
        }
    }

    private void ResetZombieSpeed()
    {
        IsZombieSlowed = false;
        IsZombieFast = false;
        Zombie.GlobalSpeedMultiplier = 1.0f;
    }

    private void UpdatePickupSpawning(float thetaSync, float gammaSync)
    {
        if (isOnCooldown)
        {
            cooldownTimer += Time.deltaTime;
            if (cooldownTimer >= spawnCooldown)
            {
                cooldownTimer = 0f;
                isOnCooldown = false;
            }
            return;
        }

        // Wysoka synchronia Theta (skupienie/pamięć) lub Gamma (wspólna uwaga) spawnuje pomoc
        if (thetaSync >= criticalThetaSync || gammaSync >= criticalGammaSync)
        {
            SpawnRandomPickup();
            isOnCooldown = true;
        }
    }

    private void OnValidate()
    {
        if (pickupPrefabs == null) return;

        for (int i = 0; i < pickupPrefabs.Length; i++)
        {
            if (pickupPrefabs[i] != null && pickupPrefabs[i].GetComponentInChildren<SpawnedObject>() == null)
            {
                Debug.LogWarning($"[{nameof(BciGameDriver)}] Prefab '{pickupPrefabs[i].name}' nie posiada żadnego skryptu dziedziczącego po SpawnedObject!", this);
            }
        }
    }

    private void SpawnRandomPickup()
    {
        if (spawnPoints == null || spawnPoints.Length == 0 || pickupPrefabs == null || pickupPrefabs.Length == 0)
        {
            Debug.LogWarning($"[{nameof(BciGameDriver)}] Brak przypisanych punktów spawnu lub prefabów przedmiotów!", this);
            return;
        }

        Transform chosenPoint = spawnPoints[Random.Range(0, spawnPoints.Length)];
        GameObject chosenPrefab = pickupPrefabs[Random.Range(0, pickupPrefabs.Length)];

        if (chosenPoint != null && chosenPrefab != null)
        {
            Instantiate(chosenPrefab, chosenPoint.position, Quaternion.identity);
        }
    }
}