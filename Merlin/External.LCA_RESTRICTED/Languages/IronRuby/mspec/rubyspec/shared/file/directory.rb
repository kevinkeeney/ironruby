describe :file_directory, :shared => true do
  before :each do
    platform_is :windows do
      @dir  = "#{ENV['SystemDrive']}\\"
      @file = "#{ENV['SystemRoot']}\\notepad.exe"
    end

    platform_is_not :windows do
      @dir  = "/"
      @file = "/bin/ls"
    end
  end

  after :each do
    @dir = nil
  end

  it "return true if dir is a directory, otherwise return false" do
    @object.send(@method, @dir).should == true
    @object.send(@method, @file).should == false
  end

  ruby_version_is "1.9" do
    it "accepts an object that has a #to_path method" do
      @object.send(@method, mock_to_path(@dir)).should == true
    end
  end

  it "raises an ArgumentError if not passed one argument" do
    lambda { @object.send(@method)              }.should raise_error(ArgumentError)
    lambda { @object.send(@method, @dir, @file) }.should raise_error(ArgumentError)
  end

  it "raises a TypeError if not passed a String type" do
    lambda { @object.send(@method, nil) }.should raise_error(TypeError)
  end
end
