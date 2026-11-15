using UnityEngine;
using UnityEngine.SceneManagement;

public class MainMenu : MonoBehaviour
{
    [Tooltip("Exact name of the scene to load when clicking Start")]
    public string sceneToLoad = "Level1";

    // Assign this method to the START button
    public void StartGame()
    {
        SceneManager.LoadScene(sceneToLoad);
    }

    // Assign this method to the QUIT button
    public void QuitGame()
    {
        Debug.Log("Quitting game...");
        Application.Quit();

        // Allows testing the quit functionality directly inside the Unity Editor
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }
}