using Unity.Sentis;
using UnityEngine;
using System.Collections.Generic;
using System.Collections; // IEnumerator 사용
using System; // Action 사용

public class YOLOProcessor : IModelProcessor
{
    // YOLO 모델의 기본 입력 크기 (필요시 모델에서 직접 읽어오도록 수정 가능)
    private const int YoloInputWidth = 640;
    private const int YoloInputHeight = 640;

    public int InputWidth { get; private set; }
    public int InputHeight { get; private set; }

    private string[] labels;
    private Worker worker;
    private Tensor<float> centersToCorners;
    private float currentIouThreshold;
    private float currentScoreThreshold;

    public void LoadModel(ModelAsset modelAsset, TextAsset classesAsset, BackendType backend, float iouThreshold, float scoreThreshold)
    {
        this.labels = classesAsset.text.Split('\n');
        this.currentIouThreshold = iouThreshold;
        this.currentScoreThreshold = scoreThreshold;

        // 모델 로드 후 입력 크기 설정
        // 실제로는 모델 에셋(modelAsset.inputs[0].shape)에서 읽어오는 것이 가장 정확합니다.
        // 여기서는 YOLO의 일반적인 크기를 사용합니다.
        InputWidth = YoloInputWidth;
        InputHeight = YoloInputHeight;

        var model = ModelLoader.Load(modelAsset);

        // 모델 입력 레이어의 shape을 확인하여 InputWidth/Height를 설정할 수도 있습니다.
        // 예: var inputShape = model.inputs[0].shape;
        // InputWidth = inputShape[3]; // assuming NCHW or NHWC, check layout
        // InputHeight = inputShape[2];

        centersToCorners = new Tensor<float>(new TensorShape(4, 4),
        new float[]
        {
            1,      0,      1,      0,
            0,      1,      0,      1,
            -0.5f,  0,      0.5f,   0,
            0,      -0.5f,  0,      0.5f
        });

        var graph = new FunctionalGraph();
        var inputs = graph.AddInputs(model);
        var modelOutput = Functional.Forward(model, inputs)[0];
        var boxCoords = modelOutput[0, 0..4, ..].Transpose(0, 1);
        var allScores = modelOutput[0, 4.., ..];
        var scores = Functional.ReduceMax(allScores, 0);
        var classIDs = Functional.ArgMax(allScores, 0);
        var boxCorners = Functional.MatMul(boxCoords, Functional.Constant(centersToCorners));
        var indices = Functional.NMS(boxCorners, scores, this.currentIouThreshold, this.currentScoreThreshold);
        var finalCoords = Functional.IndexSelect(boxCoords, 0, indices);
        var finalLabelIDs = Functional.IndexSelect(classIDs, 0, indices);
        var finalScores = Functional.IndexSelect(scores, 0, indices);

        worker = new Worker(graph.Compile(finalCoords, finalLabelIDs, finalScores), backend);
    }

    // Process 메서드를 IEnumerator를 반환하고 콜백을 사용하는 비동기 방식으로 변경
    public IEnumerator Process(Texture sourceTexture, RenderTexture targetRT, Action<List<Detection>> onCompleted)
    {
        if (sourceTexture == null)
        {
            Debug.LogError("Source texture is not assigned in YOLOProcessor");
            onCompleted?.Invoke(new List<Detection>());
            yield break; 
        }

        // GPU 작업 스케줄링 (Blit, ToTensor, Schedule)
        // 이 부분은 예외 발생 가능성이 낮거나, 발생 시 복구가 어려울 수 있음
        float sourceAspect = (float)sourceTexture.width / sourceTexture.height;
        float targetCanvasAspect = (float)InputWidth / InputHeight;
        Vector2 scale = Vector2.one;
        Vector2 offset = Vector2.zero;
        if (sourceAspect > targetCanvasAspect)
        {
            scale.y = targetCanvasAspect / sourceAspect;
            offset.y = (1 - scale.y) / 2f;
        }
        else
        {
            scale.x = sourceAspect / targetCanvasAspect;
            offset.x = (1 - scale.x) / 2f;
        }
        Graphics.Blit(sourceTexture, targetRT, scale, offset);
        using var inputTensor = new Tensor<float>(new TensorShape(1, 3, InputHeight, InputWidth));
        TextureConverter.ToTensor(targetRT, inputTensor, default);
        worker.Schedule(inputTensor);

        // 작업이 GPU에서 처리될 시간을 줌 (메인 스레드 차단 없이)
        yield return null; 

        // 이제 결과를 읽어오는 부분을 try-catch-finally로 감쌉니다.
        List<Detection> detections = new List<Detection>();
        try
        {
            Tensor<float> foundBoxesTensor = worker.PeekOutput("output_0") as Tensor<float>;
            Tensor<int> labelIDsOutputTensor = worker.PeekOutput("output_1") as Tensor<int>;
            Tensor<float> scoresOutputTensor = worker.PeekOutput("output_2") as Tensor<float>;

            if (foundBoxesTensor == null || labelIDsOutputTensor == null || scoresOutputTensor == null)
            {
                Debug.LogError("모델 결과 텐서를 가져오는 데 실패했습니다. PeekOutput에서 null이 반환되었습니다.");
                // detections는 이미 빈 리스트로 초기화되어 있으므로 추가 작업 필요 없음
            }
            else
            {
                using var foundBoxes = foundBoxesTensor.ReadbackAndClone();
                using var labelIDsOutput = labelIDsOutputTensor.ReadbackAndClone();
                using var scoresOutput = scoresOutputTensor.ReadbackAndClone();

                int boxesFoundCount = foundBoxes.shape[0];
                if (boxesFoundCount > 0)
                {
                    for (int i = 0; i < boxesFoundCount; i++)
                    {
                        float currentScore = scoresOutput[i];
                        detections.Add(new Detection
                        {
                            Label = labels[labelIDsOutput[i]],
                            Score = currentScore,
                            BoundingBox = new Rect(
                                foundBoxes[i, 0] - foundBoxes[i, 2] / 2f, 
                                foundBoxes[i, 1] - foundBoxes[i, 3] / 2f, 
                                foundBoxes[i, 2],                         
                                foundBoxes[i, 3]                          
                            )
                        });
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"YOLOProcessor.Process 결과 처리 중 오류 발생: {e.Message}\n{e.StackTrace}");
            detections = new List<Detection>(); // 오류 시 빈 리스트 보장
        }
        finally
        {
            onCompleted?.Invoke(detections); // 성공하든 실패하든 콜백 호출
        }
    }

    public void Dispose()
    {
        worker?.Dispose();
        centersToCorners?.Dispose();
    }
} 