using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

[RequireComponent(typeof(AudioSource))]
public class GameUIController : MonoBehaviour
{
    [Header("Referencje w scenie")]
    [SerializeField] private RoundSpawner roundSpawner;
    [SerializeField] private PlayerHealth player1Health;
    [Tooltip("Opcjonalne — przypisz, jeœli wyœwietlasz ¿ycia tak¿e dla 2. gracza")]
    [SerializeField] private PlayerHealth player2Health;

    [Header("Elementy UI (TextMeshPro)")]
    [SerializeField] private TMP_Text aliveZombiesText;
    [SerializeField] private TMP_Text gameTimerText;
    [Tooltip("Du¿y napis na œrodku ekranu do komunikatów o rundzie")]
    [SerializeField] private TMP_Text roundAnnouncementText;
    [SerializeField] private float announcementDuration = 2.5f;

    [Header("Ikony ¿yæ (UI Image)")]
    [Tooltip("Przypisz po kolei 3 obrazki (np. serca) dla Gracza 1")]
    [SerializeField] private Image[] player1LifeIcons;
    [Tooltip("Opcjonalnie przypisz po kolei 3 obrazki dla Gracza 2")]
    [SerializeField] private Image[] player2LifeIcons;

    [Header("Ekran Przegranej (Game Over)")]
    [Tooltip("Panel UI z napisem o przegranej i przyciskiem powrotu do menu")]
    [SerializeField] private GameObject gameOverPanel;
    [SerializeField] private string mainMenuSceneName = "MainMenu";
    [SerializeField] private bool unlockCursorOnGameOver = true;

    [Header("DŸwiêki UI")]
    [SerializeField] private AudioClip roundWonSound;
    [SerializeField, Range(0f, 1f)] private float roundWonVolume = 0.8f;
    [SerializeField] private AudioClip gameOverSound;
    [SerializeField, Range(0f, 1f)] private float gameOverVolume = 0.8f;

    public static bool IsGameOver { get; private set; }

    private AudioSource audioSource;
    private float elapsedTime = 0f;
    private Coroutine announcementCoroutine;

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f; // Wymuszenie dŸwiêku 2D dla UI
    }

    private void OnEnable()
    {
        if (player1Health != null)
            player1Health.OnLivesChanged += UpdatePlayer1LivesUI;

        if (player2Health != null)
            player2Health.OnLivesChanged += UpdatePlayer2LivesUI;

        if (roundSpawner != null)
        {
            roundSpawner.OnRoundStarted += ShowRoundStarted;
            roundSpawner.OnRoundWon += ShowRoundWon;
        }
    }

    private void OnDisable()
    {
        if (player1Health != null)
            player1Health.OnLivesChanged -= UpdatePlayer1LivesUI;

        if (player2Health != null)
            player2Health.OnLivesChanged -= UpdatePlayer2LivesUI;

        if (roundSpawner != null)
        {
            roundSpawner.OnRoundStarted -= ShowRoundStarted;
            roundSpawner.OnRoundWon -= ShowRoundWon;
        }
    }

    private void Start()
    {
        IsGameOver = false;

        if (gameOverPanel != null)
            gameOverPanel.SetActive(false);

        if (roundAnnouncementText != null)
            roundAnnouncementText.gameObject.SetActive(false);

        if (player1Health != null)
            UpdatePlayer1LivesUI(player1Health.CurrentLives);

        if (player2Health != null)
            UpdatePlayer2LivesUI(player2Health.CurrentLives);
    }

    private void Update()
    {
        if (IsGameOver) return;

        UpdateTimer();
        UpdateZombiesCount();
    }

    private void UpdateTimer()
    {
        if (gameTimerText == null) return;

        elapsedTime += Time.deltaTime;
        int minutes = Mathf.FloorToInt(elapsedTime / 60f);
        int seconds = Mathf.FloorToInt(elapsedTime % 60f);

        gameTimerText.text = $"{minutes:00}:{seconds:00}";
    }

    private void UpdateZombiesCount()
    {
        if (aliveZombiesText == null || roundSpawner == null) return;

        aliveZombiesText.text = $"{roundSpawner.ZombiesAlive}";
    }

    private void ShowRoundStarted(int roundNumber)
    {
        ShowAnnouncement($"ROUND {roundNumber}");
    }

    private void ShowRoundWon()
    {
        ShowAnnouncement("ROUND WON");

        if (roundWonSound != null)
        {
            audioSource.PlayOneShot(roundWonSound, roundWonVolume);
        }
    }

    private void ShowAnnouncement(string message)
    {
        if (roundAnnouncementText == null) return;

        if (announcementCoroutine != null)
            StopCoroutine(announcementCoroutine);

        announcementCoroutine = StartCoroutine(AnnouncementRoutine(message));
    }

    private IEnumerator AnnouncementRoutine(string message)
    {
        roundAnnouncementText.text = message;
        roundAnnouncementText.gameObject.SetActive(true);

        yield return new WaitForSeconds(announcementDuration);

        roundAnnouncementText.gameObject.SetActive(false);
        announcementCoroutine = null;
    }

    private void UpdatePlayer1LivesUI(int currentLives)
    {
        UpdateLifeIcons(player1LifeIcons, currentLives);
        CheckGameOver();
    }

    private void UpdatePlayer2LivesUI(int currentLives)
    {
        UpdateLifeIcons(player2LifeIcons, currentLives);
        CheckGameOver();
    }

    private void UpdateLifeIcons(Image[] icons, int currentLives)
    {
        if (icons == null) return;

        for (int i = 0; i < icons.Length; i++)
        {
            if (icons[i] != null)
            {
                icons[i].enabled = i < currentLives;
            }
        }
    }

    private void CheckGameOver()
    {
        if (IsGameOver) return;

        bool p1Dead = player1Health == null || player1Health.CurrentLives <= 0;
        bool p2Dead = player2Health == null || player2Health.CurrentLives <= 0;

        if (p1Dead && p2Dead)
        {
            TriggerGameOver();
        }
    }

    private void TriggerGameOver()
    {
        IsGameOver = true;

        if (gameOverSound != null)
        {
            audioSource.PlayOneShot(gameOverSound, gameOverVolume);
        }

        if (gameOverPanel != null)
            gameOverPanel.SetActive(true);

        Time.timeScale = 0f;

        if (unlockCursorOnGameOver)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    public void ReturnToMainMenu()
    {
        Time.timeScale = 1f;
        IsGameOver = false;
        SceneManager.LoadScene(mainMenuSceneName);
    }

    public void RestartGame()
    {
        Time.timeScale = 1f;
        IsGameOver = false;
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }
}