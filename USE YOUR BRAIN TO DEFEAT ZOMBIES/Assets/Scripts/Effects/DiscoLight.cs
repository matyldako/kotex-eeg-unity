using UnityEngine;

[RequireComponent(typeof(Light))]
public class DiscoLight : MonoBehaviour
{
    [Header("Zmiana kolorów")]
    [Tooltip("Szybkoœæ przechodzenia miêdzy kolorami têczy")]
    [SerializeField] private float colorCycleSpeed = 0.5f;
    [Range(0f, 1f)][SerializeField] private float saturation = 1f;
    [Range(0f, 1f)][SerializeField] private float brightness = 1f;

    [Header("Pulsowanie / Stroboskop")]
    [SerializeField] private bool enablePulse = true;
    [SerializeField] private float minIntensity = 2f;
    [SerializeField] private float maxIntensity = 8f;
    [SerializeField] private float pulseSpeed = 6f;

    [Header("Obrót reflektora")]
    [SerializeField] private bool enableRotation = true;
    [SerializeField] private Vector3 rotationSpeed = new Vector3(0f, 90f, 0f);

    private Light discoLight;
    private float hue;

    private void Awake()
    {
        discoLight = GetComponent<Light>();
        // Losowy startowy kolor, aby kilka œwiate³ nie œwieci³o identycznie
        hue = Random.value;
    }

    private void Update()
    {
        // 1. P³ynna zmiana koloru (HSV)
        hue = Mathf.Repeat(hue + colorCycleSpeed * Time.deltaTime, 1f);
        discoLight.color = Color.HSVToRGB(hue, saturation, brightness);

        // 2. Pulsowanie jasnoœci
        if (enablePulse)
        {
            float t = (Mathf.Sin(Time.time * pulseSpeed) + 1f) * 0.5f;
            discoLight.intensity = Mathf.Lerp(minIntensity, maxIntensity, t);
        }

        // 3. Obracanie reflektora
        if (enableRotation)
        {
            transform.Rotate(rotationSpeed * Time.deltaTime, Space.Self);
        }
    }
}