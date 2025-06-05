using UnityEngine;
using Unity.Sentis;
// using UnityEngine.UIElements; // 사용하지 않으므로 주석 처리 또는 삭제
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Events; // List<Detection> 사용을 위해 추가
using System;
using UnityEngine.UI; // Action 사용을 위해 추가

// 사용할 모델 타입을 정의하는 Enum
public enum ModelType
{
    YOLO,
    EfficientDet // 예시로 추가
    // 여기에 다른 모델 타입을 추가할 수 있습니다.
}

    public struct BoundingBox
    {
        public float centerX;
        public float centerY;
        public float width;
        public float height;
        public string label;
    }

public class ModelManager : MonoBehaviour
{
    [Header("Model Configuration")]
    [Tooltip("사용할 모델의 종류를 선택하세요.")]
    [SerializeField] ModelType selectedModelType = ModelType.YOLO;
    [SerializeField] ModelAsset modelAsset;        // 사용할 모델 에셋
    [SerializeField] TextAsset classesAsset;      // 모델의 클래스 레이블 파일
    [SerializeField] BackendType backendType = BackendType.GPUCompute;

    [Header("Reference")]
    [SerializeField] Texture2D borderTexture;
    [SerializeField] Transform displayLocation;

    [Header("Input Configuration")]
    [SerializeField] Texture sourceTexture;       // 입력으로 사용할 텍스처
    private RenderTexture targetRT;             // 모델 입력 크기로 리사이즈될 RenderTexture
    private Sprite borderSprite;

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
    private List<GameObject> currentBoxesOnScreen = new List<GameObject>(); // 그려진 박스 추적

    public UnityEvent<string> OnDetectionResult;

    void Start()
    {
        InitializeModel();
        if (borderTexture != null)
        {
            borderSprite = Sprite.Create(borderTexture, new Rect(0, 0, borderTexture.width, borderTexture.height), new Vector2(borderTexture.width / 2, borderTexture.height / 2));
        }
        else
        {
            Debug.LogError("BorderTexture가 할당되지 않았습니다. ModelManager에서 확인해주세요.");
        }

        if (displayLocation == null)
        {
            Debug.LogError("DisplayLocation이 할당되지 않았습니다. ModelManager에서 박스를 그릴 부모 UI Transform을 할당해주세요.");
        }
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

            // 이전 박스들 제거
            foreach (var oldBox in currentBoxesOnScreen)
            {
                Destroy(oldBox);
            }
            currentBoxesOnScreen.Clear();

            if (displayLocation == null || modelProcessor == null)
            {
                Debug.LogError("DisplayLocation 또는 ModelProcessor가 초기화되지 않았습니다.");
                return;
            }
            
            RectTransform displayRectTransform = displayLocation.GetComponent<RectTransform>();
            if (displayRectTransform == null)
            {
                Debug.LogError("DisplayLocation에 RectTransform 컴포넌트가 없습니다.");
                return;
            }

            float displayWidthOnUI = displayRectTransform.rect.width;
            float displayHeightOnUI = displayRectTransform.rect.height;

            float modelInputWidth = modelProcessor.InputWidth;
            float modelInputHeight = modelProcessor.InputHeight;

            if (modelInputWidth <= 0 || modelInputHeight <= 0)
            {
                Debug.LogError("모델 입력 크기가 유효하지 않습니다.");
                return;
            }

            float scaleX = displayWidthOnUI / modelInputWidth;
            float scaleY = displayHeightOnUI / modelInputHeight;
            float baseFontSize = displayHeightOnUI * 0.05f;

            string resultTextForLog = ""; // 로그 및 이벤트용 텍스트

            if (lastDetections.Count > 0)
            {
                // 모든 탐지된 객체에 대해 박스 그리기
                for (int i = 0; i < lastDetections.Count; i++)
                {
                    Detection d = lastDetections[i];

                    float model_x_min = d.BoundingBox.xMin;
                    float model_y_min = d.BoundingBox.yMin;
                    float model_width = d.BoundingBox.width;
                    float model_height = d.BoundingBox.height;

                    // 모델 좌표계의 중심 계산
                    float model_x_center = model_x_min + model_width / 2f;
                    float model_y_center = model_y_min + model_height / 2f;

                    // UI 좌표계로 스케일링 (displayLocation의 좌상단 기준)
                    float ui_x_center_abs = model_x_center * scaleX;
                    float ui_y_center_abs = model_y_center * scaleY;
                    float ui_width = model_width * scaleX;
                    float ui_height = model_height * scaleY;

                    // displayLocation의 중심을 (0,0)으로 하는 로컬 좌표로 변환
                    float box_pos_x = ui_x_center_abs - (displayWidthOnUI / 2f);
                    float box_pos_y = ui_y_center_abs - (displayHeightOnUI / 2f);


                    var uiBox = new BoundingBox
                    {
                        centerX = box_pos_x,
                        centerY = box_pos_y, // DrawBox에서 Y값을 반전시킴 (new Vector3(box.centerX, -box.centerY))
                        width = ui_width,
                        height = ui_height,
                        label = d.Label // Detection 구조체에서 Label 사용
                    };

                    GameObject newBoxGO = DrawBox(uiBox, i, baseFontSize);
                    if (newBoxGO != null)
                    {
                        currentBoxesOnScreen.Add(newBoxGO);
                    }
                }

                // 가장 높은 점수의 객체 정보 로깅 (기존 로직 유지)
                Detection bestDetection = lastDetections[0];
                foreach (var detection in lastDetections)
                {
                    if (detection.Score > bestDetection.Score)
                    {
                        bestDetection = detection;
                    }
                }
                resultTextForLog = $"[ModelManager] 가장 높은 점수의 객체: {bestDetection.Label}, 점수: {bestDetection.Score:P2}. 총 {lastDetections.Count}개 탐지.";
            }
            else
            {
                resultTextForLog = "[ModelManager] 탐지된 객체가 없습니다.";
            }
            Debug.Log(resultTextForLog);
            OnDetectionResult?.Invoke(resultTextForLog);
        });
    }

    public GameObject DrawBox(BoundingBox box, int id, float fontSize)
    {
        if (displayLocation == null || borderSprite == null)
        {
            Debug.LogError("DrawBox: displayLocation 또는 borderSprite가 설정되지 않았습니다.");
            return null;
        }
        // var panel = CreateNewBox(Color.yellow); // 이전 호출
        var panel = CreateNewBox(Color.yellow, box.label, (int)fontSize);

        panel.transform.localPosition = new Vector3(box.centerX, -box.centerY); // Y축 반전 적용
        RectTransform rt = panel.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(box.width, box.height);
        
        // Text 설정은 CreateNewBox에서 처리됨
        return panel;
    }

    public GameObject CreateNewBox(Color color, string labelText, int labelFontSize)
    {
        if (displayLocation == null || borderSprite == null)
        {
             Debug.LogError("CreateNewBox: displayLocation 또는 borderSprite가 설정되지 않았습니다.");
            return null;
        }

        var panel = new GameObject("ObjectBox_" + labelText);
        panel.AddComponent<CanvasRenderer>();
        Image img = panel.AddComponent<Image>();
        img.color = color; 
        img.sprite = borderSprite;
        img.type = Image.Type.Sliced;
        panel.transform.SetParent(displayLocation, false);

        // Text 자식 GameObject 생성 및 설정
        GameObject textObject = new GameObject("Label");
        textObject.transform.SetParent(panel.transform, false);
        Text label = textObject.AddComponent<Text>();
        label.text = labelText;
        
        // 시스템 기본 폰트 또는 프로젝트에 포함된 폰트 사용 (Arial이 없을 수 있으므로 변경)
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); 
        if (label.font == null) { // 폴백 폰트도 없을 경우
            Debug.LogWarning("기본 UI 폰트를 찾을 수 없습니다. Text가 표시되지 않을 수 있습니다.");
        }

        label.fontSize = labelFontSize;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = Color.black; // 텍스트 색상

        // Text RectTransform 설정 (패널 채우도록)
        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero; 
        textRect.offsetMax = Vector2.zero;

        return panel;
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
