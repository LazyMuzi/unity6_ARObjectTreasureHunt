using UnityEngine;
using Unity.Sentis;
// using UnityEngine.UIElements; // 사용하지 않으므로 주석 처리 또는 삭제
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Events; // List<Detection> 사용을 위해 추가
using System; // Action 사용을 위해 추가

// 사용할 모델 타입을 정의하는 Enum
public enum ModelType
{
    YOLO,
    EfficientDet // 예시로 추가
    // 여기에 다른 모델 타입을 추가할 수 있습니다.
}

public class ModelManager : MonoBehaviour
{
    [Header("Model Configuration")]
    [Tooltip("사용할 모델의 종류를 선택하세요.")]
    [SerializeField] ModelType selectedModelType = ModelType.YOLO;
    [SerializeField] ModelAsset modelAsset;        // 사용할 모델 에셋
    [SerializeField] TextAsset classesAsset;      // 모델의 클래스 레이블 파일
    [SerializeField] BackendType backendType = BackendType.GPUCompute;

    [Header("Input Configuration")]
    [SerializeField] Texture sourceTexture;       // 입력으로 사용할 텍스처
    private RenderTexture targetRT;             // 모델 입력 크기로 리사이즈될 RenderTexture

    [Header("Detection Parameters")]
    [SerializeField, Range(0, 1)] float iouThreshold = 0.45f;
    [SerializeField, Range(0, 1)] float scoreThreshold = 0.5f;

    // 모델 입력 이미지 크기는 이제 각 Processor가 내부적으로 알거나, 설정 가능해야 합니다.
    // ModelManager에서는 공통된 크기를 사용하지 않도록 변경할 수 있지만, 일단 유지하고 Processor에서 활용합니다.
    // const int imageWidth = 640; // YOLO 기준, 다른 모델은 다를 수 있음
    // const int imageHeight = 640; // YOLO 기준, 다른 모델은 다를 수 있음

    private IModelProcessor modelProcessor;     // 현재 선택된 모델 처리기
    private bool isProcessing = false;          // 코루틴 실행 상태 플래그
    private List<Detection> lastDetections = new List<Detection>(); // 마지막 탐지 결과 저장

    public UnityEvent<string> OnDetectionResult;

    void Start()
    {
        InitializeModel();
    }

    public void InitializeModel()
    {
        // 선택된 모델 타입에 따라 적절한 프로세서 생성
        switch (selectedModelType)
        {
            case ModelType.YOLO:
                modelProcessor = new YOLOProcessor();
                break;
            case ModelType.EfficientDet:
                modelProcessor = new EfficientDetProcessor(); 
                break;
            default:
                Debug.LogError($"선택된 모델 타입({selectedModelType})에 대한 처리기가 정의되지 않았습니다.");
                enabled = false;
                return;
        }

        if (modelAsset == null || classesAsset == null)
        {
            Debug.LogError("ModelAsset 또는 ClassesAsset이 할당되지 않았습니다.");
            enabled = false;
            return;
        }

        if (modelProcessor == null)
        {
            Debug.LogError($"모델 프로세서({selectedModelType})가 초기화되지 않았습니다. EfficientDet을 선택한 경우 해당 프로세서가 준비되었는지 확인하세요.");
            enabled = false;
            return;
        }

        modelProcessor.LoadModel(modelAsset, classesAsset, backendType, iouThreshold, scoreThreshold);
        Debug.Log($"ModelManager 시작됨. 선택된 프로세서: {modelProcessor.GetType().Name}, 입력 크기: {modelProcessor.InputWidth}x{modelProcessor.InputHeight}");

        if (modelProcessor.InputWidth > 0 && modelProcessor.InputHeight > 0)
        {
            targetRT = new RenderTexture(modelProcessor.InputWidth, modelProcessor.InputHeight, 0);
        }
        else
        {
            Debug.LogError("모델 프로세서에서 유효한 입력 크기를 가져오지 못했습니다. targetRT를 생성할 수 없습니다.");
            enabled = false;
            return;
        }
    }

    public void ExecuteDetection(Texture sourceTexture)
    {
        if (sourceTexture == null)
        {
            Debug.LogError("입력된 sourceTexture가 null입니다.");
            return;
        }

        if (!enabled) // 컴포넌트가 비활성화 상태이면 실행하지 않음
        {
            Debug.LogWarning("ModelManager가 비활성화되어 있어 탐지를 실행할 수 없습니다.");
            return;
        }

        if (!isProcessing && modelProcessor != null && targetRT != null)
        {
            StartCoroutine(ExecuteDetectionCoroutine(sourceTexture));
        }
        else if (isProcessing)
        {
            Debug.LogWarning("ModelManager가 이미 다른 이미지를 처리 중입니다.");
        }
        else
        {
            Debug.LogError("ModelManager가 올바르게 초기화되지 않았거나 (modelProcessor 또는 targetRT가 null), 탐지를 실행할 수 없습니다.");
        }
    }

    IEnumerator ExecuteDetectionCoroutine(Texture sourceTexture)
    {
        isProcessing = true;

        yield return modelProcessor.Process(sourceTexture, targetRT, (detections) => 
        {
            lastDetections = detections ?? new List<Detection>();
            isProcessing = false;

            var resultText = "";
            if (lastDetections.Count > 0)
            {
                Detection bestDetection = lastDetections[0];
                foreach (var detection in lastDetections)
                {
                    if (detection.Score > bestDetection.Score)
                    {
                        bestDetection = detection;
                    }
                }
                resultText = $"[ModelManager] 가장 높은 점수의 객체: {bestDetection.Label}, 점수: {bestDetection.Score:P2}, 위치: {bestDetection.BoundingBox}";
            }
            else
            {
                resultText = "[ModelManager] 탐지된 객체가 없습니다.";
            }
            Debug.Log(resultText);
            OnDetectionResult?.Invoke(resultText);
        });
    }

    void OnGUI()
    {
        if (lastDetections != null && lastDetections.Count > 0 && targetRT != null)
        {
            // GUI 로직 (필요시 사용)
        }
    }

    private void OnDisable()
    {
        modelProcessor?.Dispose();
        if (targetRT != null)
        {
            targetRT.Release();
            Destroy(targetRT);
            targetRT = null;
        }
        Debug.Log("ModelManager 비활성화 및 리소스 해제됨");
    }
}
