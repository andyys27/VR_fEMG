using UnityEngine;
using TMPro;
using System.Collections;

public class ChangeCubeContent : MonoBehaviour
{
    [Header("Object to change")]
    public Renderer cubeRenderer;

    [Header("Gesture Materials")]
    public Material[] gestureMaterials;

    [Header("Gesture Texts")]
    public string[] gestureTexts;

    [Header("Text Object")]
    public TMP_Text textObject;

    [Header("Countdown Text Object")]
    public TMP_Text countdownText;

    [Header("Settings")]
    public float initialRestDuration = 5f;
    public float gestureHoldAfterCountdown = 3f;
    public float restDuration = 5f;
    public int repetitions = 5;

    // Contexto que se adjunta a cada evento del log
    private int _trial = -1;     // 0..(repetitions*gestos - 1); -1 = aún no empieza
    private int _rep = -1;
    private int _gesture = -1;

    private void Start()
    {
        StartCoroutine(RunSequence());
    }

    IEnumerator RunSequence()
    {
        if (gestureMaterials.Length == 0)
        {
            Debug.LogError("No hay materiales de gestos asignados.");
            yield break;
        }

        if (gestureTexts.Length != gestureMaterials.Length)
        {
            Debug.LogWarning("La cantidad de textos no coincide con la cantidad de materiales.");
        }

        if (countdownText != null)
        {
            countdownText.gameObject.SetActive(false);
        }

        ExperimentLogger.Log("sequence_start",
            "reps=" + repetitions +
            ";gestures=" + gestureMaterials.Length +
            ";initial_rest_s=" + initialRestDuration +
            ";countdown_s=3" +
            ";hold_s=" + gestureHoldAfterCountdown +
            ";rest_s=" + restDuration);

        // Empieza en reposo
        ShowRest("initial");
        yield return new WaitForSeconds(initialRestDuration);

        for (int rep = 0; rep < repetitions; rep++)
        {
            for (int i = 0; i < gestureMaterials.Length; i++)
            {
                _rep = rep;
                _gesture = i;
                _trial = rep * gestureMaterials.Length + i;

                // Muestra gesto con su texto
                ShowGesture(i);

                // Contador 3..2..1
                yield return StartCoroutine(Countdown());

                // "GO": aquí el participante debe hacer el gesto
                ExperimentLogger.Log("hold_start", Ctx());

                // Mantiene el gesto después del contador
                yield return new WaitForSeconds(gestureHoldAfterCountdown);

                // Reposo sin cubo (equivale al fin del gesto)
                ShowRest("after_gesture");

                yield return new WaitForSeconds(restDuration);
            }
        }

        ShowRest("end");
        ExperimentLogger.Log("sequence_end", "");
        Debug.Log("Sequence finished.");
    }

    IEnumerator Countdown()
    {
        if (countdownText == null)
        {
            yield break;
        }

        countdownText.gameObject.SetActive(true);

        countdownText.text = "3";
        ExperimentLogger.Log("countdown_3", Ctx());
        yield return new WaitForSeconds(1f);

        countdownText.text = "2";
        ExperimentLogger.Log("countdown_2", Ctx());
        yield return new WaitForSeconds(1f);

        countdownText.text = "1";
        ExperimentLogger.Log("countdown_1", Ctx());
        yield return new WaitForSeconds(1f);

        countdownText.gameObject.SetActive(false);
    }

    void ShowGesture(int index)
    {
        if (cubeRenderer != null && index < gestureMaterials.Length)
        {
            cubeRenderer.enabled = true;
            cubeRenderer.material = gestureMaterials[index];
        }

        if (textObject != null)
        {
            if (gestureTexts != null && index < gestureTexts.Length && gestureTexts[index] != "")
            {
                textObject.text = gestureTexts[index];
            }
            else
            {
                textObject.text = "Haz este gesto";
            }
        }

        ExperimentLogger.Log("gesture_show", Ctx());
    }

    void ShowRest(string reason)
    {
        if (cubeRenderer != null)
        {
            cubeRenderer.enabled = false;
        }

        if (textObject != null)
        {
            textObject.text = "Relaja";
        }

        if (countdownText != null)
        {
            countdownText.gameObject.SetActive(false);
        }

        ExperimentLogger.Log("rest_start", "reason=" + reason + ";" + Ctx());
    }

    // trial=3;rep=0;gesture_idx=3;gesture=Triste
    string Ctx()
    {
        string name = "none";
        if (_gesture >= 0 && gestureMaterials != null && _gesture < gestureMaterials.Length && gestureMaterials[_gesture] != null)
        {
            name = gestureMaterials[_gesture].name;
        }
        return "trial=" + _trial + ";rep=" + _rep + ";gesture_idx=" + _gesture + ";gesture=" + name;
    }
}