using System.Collections;
using UnityEngine;

[RequireComponent(typeof(RectTransform))]
public class SlideDownUI : MonoBehaviour
{
    [Header("Ustawienia animacji")]
    [Tooltip("O ile pikseli wy¿ej panel ma wystartowaæ (powy¿ej ekranu)")]
    [SerializeField] private float startOffsetY = 1080f;
    [Tooltip("Czas sp³ywania panelu w sekundach")]
    [SerializeField] private float duration = 0.6f;

    private RectTransform rectTransform;
    private Vector2 targetPosition;
    private bool isInitialized = false;

    private void Awake()
    {
        rectTransform = GetComponent<RectTransform>();
        // Zapamiêtujemy pozycjê, w której ustawi³eœ panel w edytorze
        targetPosition = rectTransform.anchoredPosition;
        isInitialized = true;
    }

    private void OnEnable()
    {
        if (!isInitialized) return;

        StopAllCoroutines();
        StartCoroutine(SlideRoutine());
    }

    private IEnumerator SlideRoutine()
    {
        Vector2 startPosition = targetPosition + new Vector2(0f, startOffsetY);
        rectTransform.anchoredPosition = startPosition;

        float elapsed = 0f;

        while (elapsed < duration)
        {
            // unscaledDeltaTime dzia³a nawet przy Time.timeScale = 0
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            // SmoothStep daje ³agodne wyhamowanie pod koniec ruchu
            float smoothT = Mathf.SmoothStep(0f, 1f, t);

            rectTransform.anchoredPosition = Vector2.Lerp(startPosition, targetPosition, smoothT);
            yield return null;
        }

        rectTransform.anchoredPosition = targetPosition;
    }
}