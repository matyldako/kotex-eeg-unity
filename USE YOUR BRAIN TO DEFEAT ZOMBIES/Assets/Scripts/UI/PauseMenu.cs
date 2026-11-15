using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;

public class PauseMenu : MonoBehaviour
{
    [Header("Sterowanie (New Input System)")]
    [Tooltip("Przypisz klawisz pauzy (np. Keyboard -> Escape lub Gamepad -> Start)")]
    [SerializeField] private InputAction pauseAction = new InputAction(type: InputActionType.Button, binding: "<Keyboard>/escape");

    [Header("Elementy UI")]
    [SerializeField] private GameObject pauseMenuPanel;

    [Header("Ustawienia sceny i kursora")]
    [SerializeField] private string mainMenuSceneName = "MainMenu";
    [SerializeField] private bool lockCursorInGame = false;

    public static bool IsGamePaused { get; private set; }

    private void OnEnable()
    {
        pauseAction.Enable();
        pauseAction.performed += OnPausePerformed;
    }

    private void OnDisable()
    {
        pauseAction.performed -= OnPausePerformed;
        pauseAction.Disable();
    }

    private void Start()
    {
        ResumeGame();
    }

    private void OnPausePerformed(InputAction.CallbackContext context)
    {
        if (IsGamePaused)
        {
            ResumeGame();
        }
        else
        {
            PauseGame();
        }
    }

    public void PauseGame()
    {
        pauseMenuPanel.SetActive(true);
        Time.timeScale = 0f;
        IsGamePaused = true;

        if (lockCursorInGame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    public void ResumeGame()
    {
        pauseMenuPanel.SetActive(false);
        Time.timeScale = 1f;
        IsGamePaused = false;

        if (lockCursorInGame)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    public void LoadMainMenu()
    {
        Time.timeScale = 1f;
        IsGamePaused = false;
        SceneManager.LoadScene(mainMenuSceneName);
    }
}