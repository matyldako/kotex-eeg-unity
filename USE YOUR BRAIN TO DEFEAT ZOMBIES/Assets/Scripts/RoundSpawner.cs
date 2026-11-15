using System;
using System.Collections;
using UnityEngine;
using UnityEngine.AI;

public class RoundSpawner : MonoBehaviour
{
    [Header("Ustawienia Prefabu i Punktów")]
    [SerializeField] private GameObject zombiePrefab;
    [Tooltip("Przypisz tutaj 4 puste obiekty wyznaczaj¹ce miejsca spawnu")]
    [SerializeField] private Transform[] spawnPoints;
    [Tooltip("Lekki rozrzut wokó³ punktu spawnu, aby zombie z paczki nie wchodzi³y w siebie")]
    [SerializeField] private float spawnSpreadRadius = 1.5f;

    [Header("Ustawienia Rund i Paczek")]
    [SerializeField] private int currentRound = 1;
    [SerializeField] private int minBatchSize = 3;
    [SerializeField] private int maxBatchSize = 6;
    [SerializeField] private float timeBetweenBatches = 4f;
    [SerializeField] private float timeBetweenRounds = 5f;

    [Header("Odradzanie Graczy po Rundzie")]
    [Tooltip("Przypisz tutaj obiekty obu graczy (z komponentem PlayerHealth)")]
    [SerializeField] private PlayerHealth[] players;
    [Tooltip("Opcjonalne punkty odrodzenia dla Graczy (jeœli puste, gracz wstanie w miejscu œmierci)")]
    [SerializeField] private Transform[] playerRespawnPoints;

    private int zombiesAlive = 0;
    private bool isSpawning = false;

    public int CurrentRound => currentRound;
    public int ZombiesAlive => zombiesAlive;

    public event Action<int> OnRoundStarted;
    public event Action OnRoundWon;
    public event Action OnAllPlayersDead;

    private void OnEnable()
    {
        Zombie.OnZombieDied += HandleZombieDied;
    }

    private void OnDisable()
    {
        Zombie.OnZombieDied -= HandleZombieDied;
    }

    private void Start()
    {
        if (zombiePrefab == null || spawnPoints == null || spawnPoints.Length == 0)
        {
            Debug.LogError($"[{nameof(RoundSpawner)}] Brak przypisanego prefabu zombie lub punktów spawnu!", this);
            return;
        }

        StartCoroutine(RoundLoop());
    }

    private void Update()
    {
        if (players == null || players.Length == 0) return;

        bool anyPlayerAlive = false;
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i] != null && players[i].CurrentLives > 0)
            {
                anyPlayerAlive = true;
                break;
            }
        }

        if (!anyPlayerAlive)
        {
            OnAllPlayersDead?.Invoke();
        }
    }

    private IEnumerator RoundLoop()
    {
        while (true)
        {
            // Przerwa przed rozpoczêciem rundy
            yield return new WaitForSeconds(timeBetweenRounds);

            // Powiadomienie UI o starcie rundy
            OnRoundStarted?.Invoke(currentRound);

            int totalZombiesInRound = 10 + currentRound * 5;
            int zombiesRemainingToSpawn = totalZombiesInRound;
            isSpawning = true;

            // Spawnowanie w losowych paczkach (domyœlnie od 3 do 6)
            while (zombiesRemainingToSpawn > 0)
            {
                int randomBatchSize = UnityEngine.Random.Range(minBatchSize, maxBatchSize + 1);
                int currentBatch = Mathf.Min(randomBatchSize, zombiesRemainingToSpawn);

                SpawnBatch(currentBatch);
                zombiesRemainingToSpawn -= currentBatch;

                if (zombiesRemainingToSpawn > 0)
                {
                    yield return new WaitForSeconds(timeBetweenBatches);
                }
            }

            isSpawning = false;

            // Czekaj, a¿ gracze zabij¹ wszystkie zombie z bie¿¹cej rundy
            yield return new WaitUntil(() => zombiesAlive <= 0);

            // Powiadomienie UI o wygraniu rundy i odrodzenie poleg³ych graczy
            OnRoundWon?.Invoke();
            ReviveDeadPlayers();

            currentRound++;
        }
    }

    private void ReviveDeadPlayers()
    {
        if (players == null) return;

        for (int i = 0; i < players.Length; i++)
        {
            if (players[i] != null && players[i].CurrentLives <= 0)
            {
                // Jeœli przypisano punkty respawnu, przenieœ gracza przed aktywacj¹
                if (playerRespawnPoints != null && i < playerRespawnPoints.Length && playerRespawnPoints[i] != null)
                {
                    players[i].transform.SetPositionAndRotation(
                        playerRespawnPoints[i].position,
                        playerRespawnPoints[i].rotation
                    );
                }

                players[i].ResetLives();
            }
        }
    }

    private void SpawnBatch(int count)
    {
        Transform chosenSpawn = spawnPoints[UnityEngine.Random.Range(0, spawnPoints.Length)];

        for (int i = 0; i < count; i++)
        {
            Vector2 randomCircle = UnityEngine.Random.insideUnitCircle * spawnSpreadRadius;
            Vector3 rawPos = chosenSpawn.position + new Vector3(randomCircle.x, 0f, randomCircle.y);

            Vector3 finalSpawnPos = rawPos;
            if (NavMesh.SamplePosition(rawPos, out NavMeshHit hit, 3.0f, NavMesh.AllAreas))
            {
                finalSpawnPos = hit.position;
            }
            else
            {
                Debug.LogWarning($"[{nameof(RoundSpawner)}] Nie znaleziono NavMesha w pobli¿u {chosenSpawn.name}!", this);
            }

            Instantiate(zombiePrefab, finalSpawnPos, chosenSpawn.rotation);
            zombiesAlive++;
        }
    }

    private void HandleZombieDied()
    {
        zombiesAlive = Mathf.Max(0, zombiesAlive - 1);
    }
}